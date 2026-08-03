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
    /// The brain, shared by the home-screen rows and the standalone playlist task.
    /// Single-seed mode powers the per-movie "Because You Watched X" rows; blended mode
    /// powers the playlist. Both use the same matching the server's /Items/Similar endpoint
    /// uses in 10.11 (shared genres and tags), ranked deterministically instead of shuffled,
    /// with watched items hidden and thin results backfilled by top-rated same-genre picks.
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
        /// Recommendations similar to ONE specific movie: the per-row brain.
        /// </summary>
        /// <param name="user">The user the row is for (watched-filtering is per user).</param>
        /// <param name="seed">The movie the row is named after.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>Ranked similar movies, capped to the configured row size.</returns>
        public IReadOnlyList<BaseItem> GetSimilarTo(User user, BaseItem seed, PluginConfiguration config)
        {
            int maxItems = Math.Max(1, config.MaxItems);
            bool? excludeWatched = config.HideWatched ? false : (bool?)null;

            Dictionary<Guid, BaseItem> pool = new Dictionary<Guid, BaseItem>();

            // IMPORTANT: this query mirrors the server's own /Items/Similar endpoint
            // field-for-field. Adding Recursive/IsPlayed to it silently DISABLES the
            // genre/tag filtering on 10.11 and you get top-rated-anything back (the
            // exact failure mode of home-sections issue #243). Watched-filtering and
            // ranking are done in code below instead.
            if (seed.Genres.Length > 0 || seed.Tags.Length > 0)
            {
                IReadOnlyList<BaseItem> similar = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    Genres = seed.Genres,
                    Tags = seed.Tags,
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    EnableTotalRecordCount = false,
                    EnableGroupByMetadataKey = true,
                    ExcludeItemIds = new[] { seed.Id },
                    OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                    Limit = maxItems * 4
                });

                foreach (BaseItem item in similar)
                {
                    if (Keep(item, seed, user, excludeWatched))
                    {
                        pool[item.Id] = item;
                    }
                }
            }

            // Genre-only backfill so the row is never one weak pick.
            if (pool.Count < Math.Max(config.MinItemsPerRow, 1) && seed.Genres.Length > 0)
            {
                IReadOnlyList<BaseItem> backfill = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    Genres = seed.Genres,
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    EnableTotalRecordCount = false,
                    EnableGroupByMetadataKey = true,
                    ExcludeItemIds = new[] { seed.Id },
                    OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                    Limit = maxItems * 4
                });

                foreach (BaseItem item in backfill)
                {
                    if (!pool.ContainsKey(item.Id) && Keep(item, seed, user, excludeWatched))
                    {
                        pool[item.Id] = item;
                    }
                }
            }

            return pool.Values
                .OrderByDescending(i => i.CommunityRating ?? 0)
                .Take(maxItems)
                .ToList();
        }

        private static bool Keep(BaseItem item, BaseItem seed, User user, bool? excludeWatched)
        {
            if (item.Id == seed.Id)
            {
                return false;
            }

            if (excludeWatched == false)
            {
                try
                {
                    if (item.IsPlayed(user, null!))
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                    // If user data can't be read, keep the item rather than drop it.
                }
            }

            return true;
        }

        /// <summary>
        /// The user's most recently played movies, newest first: the row seeds.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="count">How many seeds.</param>
        /// <returns>Recently played movies.</returns>
        public IReadOnlyList<BaseItem> GetRecentSeeds(User user, int count)
        {
            return _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsPlayed = true,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                Limit = Math.Max(1, count),
                Recursive = true
            });
        }

        /// <summary>
        /// Blended multi-seed recommendations: powers the standalone playlist.
        /// </summary>
        /// <param name="user">The user to build for.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>Ordered recommendations, capped to the configured size.</returns>
        public IReadOnlyList<BaseItem> GetRecommendations(User user, PluginConfiguration config)
        {
            int maxItems = Math.Max(1, config.MaxItems);
            IReadOnlyList<BaseItem> seeds = GetRecentSeeds(user, Math.Max(1, config.SeedCount));
            if (seeds.Count == 0)
            {
                return Array.Empty<BaseItem>();
            }

            HashSet<Guid> seedIds = seeds.Select(s => s.Id).ToHashSet();
            Dictionary<Guid, double> scores = new Dictionary<Guid, double>();
            Dictionary<Guid, BaseItem> pool = new Dictionary<Guid, BaseItem>();

            for (int s = 0; s < seeds.Count; s++)
            {
                double seedWeight = 1.0 / (s + 1);
                foreach (BaseItem item in GetSimilarTo(user, seeds[s], config))
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

            return scores
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => pool[kv.Key].CommunityRating ?? 0)
                .Select(kv => pool[kv.Key])
                .Take(maxItems)
                .ToList();
        }
    }
}
