using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.BecauseYouWatched.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.BecauseYouWatched
{
    /// <summary>
    /// The brain, shared by the home-screen row and the standalone playlist task.
    /// Builds recommendations from a user's recent watches using the same matching the
    /// server's own /Items/Similar endpoint uses in 10.11 (shared genres and tags),
    /// blended across several recent seeds, with watched items hidden and thin results
    /// backfilled by top-rated same-genre picks.
    /// </summary>
    public class RecommendationEngine
    {
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationEngine"/> class.
        /// </summary>
        public RecommendationEngine(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Produces the ranked recommendation list for a user.
        /// </summary>
        /// <param name="user">The user to build for.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>Ordered recommendations, capped to the configured size.</returns>
        public IReadOnlyList<BaseItem> GetRecommendations(User user, PluginConfiguration config)
        {
            int maxItems = Math.Max(1, config.MaxItems);
            int seedCount = Math.Max(1, config.SeedCount);
            bool? excludeWatched = config.HideWatched ? false : (bool?)null;

            IReadOnlyList<BaseItem> seeds = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsPlayed = true,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                Limit = seedCount,
                Recursive = true
            });

            if (seeds.Count == 0)
            {
                return Array.Empty<BaseItem>();
            }

            HashSet<Guid> seedIds = seeds.Select(s => s.Id).ToHashSet();
            Dictionary<Guid, double> scores = new Dictionary<Guid, double>();
            Dictionary<Guid, BaseItem> pool = new Dictionary<Guid, BaseItem>();

            // Same matching the server's /Items/Similar endpoint uses in 10.11:
            // candidates sharing the seed's genres/tags. Items that match multiple
            // recent seeds accumulate score, weighted toward the most recent seed.
            for (int s = 0; s < seeds.Count; s++)
            {
                BaseItem seed = seeds[s];
                if (seed.Genres.Length == 0 && seed.Tags.Length == 0)
                {
                    continue;
                }

                double seedWeight = 1.0 / (s + 1);

                IReadOnlyList<BaseItem> similar = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    Genres = seed.Genres,
                    Tags = seed.Tags,
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    IsPlayed = excludeWatched,
                    ExcludeItemIds = new[] { seed.Id },
                    Limit = maxItems * 2,
                    Recursive = true
                });

                foreach (BaseItem item in similar)
                {
                    if (seedIds.Contains(item.Id))
                    {
                        continue;
                    }

                    if (scores.TryGetValue(item.Id, out double existing))
                    {
                        scores[item.Id] = existing + seedWeight;
                    }
                    else
                    {
                        scores[item.Id] = seedWeight;
                        pool[item.Id] = item;
                    }
                }
            }

            // Backfill from shared genres so a row is never a single weak pick.
            if (pool.Count < Math.Max(config.MinItemsPerRow, 1))
            {
                string[] genres = seeds
                    .SelectMany(x => x.Genres)
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .Take(3)
                    .ToArray();

                if (genres.Length > 0)
                {
                    IReadOnlyList<BaseItem> backfill = _libraryManager.GetItemList(new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = new[] { BaseItemKind.Movie },
                        Genres = genres,
                        IsPlayed = excludeWatched,
                        OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
                        Limit = maxItems * 2,
                        Recursive = true
                    });

                    foreach (BaseItem item in backfill)
                    {
                        if (seedIds.Contains(item.Id) || scores.ContainsKey(item.Id))
                        {
                            continue;
                        }

                        scores[item.Id] = 0.0001;
                        pool[item.Id] = item;
                    }
                }
            }

            return scores
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => pool[kv.Key].CommunityRating ?? 0)
                .Select(kv => pool[kv.Key])
                .Take(maxItems)
                .ToList();
        }
    }
}
