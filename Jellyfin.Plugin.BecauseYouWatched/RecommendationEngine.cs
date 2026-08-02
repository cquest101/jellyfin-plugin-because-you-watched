using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.BecauseYouWatched.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.BecauseYouWatched
{
    /// <summary>
    /// The brain, shared by the home-screen row and the standalone playlist task.
    /// Builds recommendations from a user's recent watches using Jellyfin's own
    /// similarity engine (SimilarTo, the engine behind /Items/Similar), blends several
    /// recent seeds, hides watched items, and backfills thin results.
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

            for (int s = 0; s < seeds.Count; s++)
            {
                BaseItem seed = seeds[s];
                double seedWeight = 1.0 / (s + 1);

                IReadOnlyList<BaseItem> similar = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    SimilarTo = seed,
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    IsPlayed = excludeWatched,
                    Limit = maxItems * 2,
                    Recursive = true
                });

                for (int p = 0; p < similar.Count; p++)
                {
                    BaseItem item = similar[p];
                    if (seedIds.Contains(item.Id))
                    {
                        continue;
                    }

                    double add = seedWeight * (1.0 / (p + 1));
                    if (scores.TryGetValue(item.Id, out double existing))
                    {
                        scores[item.Id] = existing + add;
                    }
                    else
                    {
                        scores[item.Id] = add;
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
                .Select(kv => pool[kv.Key])
                .Take(maxItems)
                .ToList();
        }
    }
}
