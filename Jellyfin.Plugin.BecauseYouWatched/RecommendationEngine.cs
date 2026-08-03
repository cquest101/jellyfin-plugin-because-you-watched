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
    ///
    /// Ranking is weighted-overlap, computed entirely in code (Jellyfin's query-level
    /// genre/tag filters have inconsistent semantics across 10.11, so they are not
    /// trusted for ranking):
    ///  - sharing a RARE genre or tag with the seed scores high; sharing a huge genre
    ///    (e.g. Action in an action-heavy library) scores low  (inverse-frequency weight)
    ///  - matches stack: a movie sharing BOTH of a seed's genres beats one sharing one
    ///  - community rating only breaks ties, it never drives the ranking
    ///  - watched items are excluded, duplicate copies of the same movie are collapsed
    /// </summary>
    public class RecommendationEngine
    {
        private const int PoolLimit = 5000;

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
            bool hideWatched = config.HideWatched;

            IReadOnlyList<BaseItem> pool = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                EnableTotalRecordCount = false,
                EnableGroupByMetadataKey = true,
                ExcludeItemIds = new[] { seed.Id },
                Limit = PoolLimit
            });

            // Inverse-frequency weights over the library: rare genres/tags are informative,
            // ubiquitous ones are nearly worthless as similarity signals.
            Dictionary<string, int> genreFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> tagFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (BaseItem item in pool)
            {
                foreach (string g in item.Genres)
                {
                    genreFreq[g] = genreFreq.TryGetValue(g, out int c) ? c + 1 : 1;
                }

                foreach (string t in item.Tags)
                {
                    tagFreq[t] = tagFreq.TryGetValue(t, out int c) ? c + 1 : 1;
                }
            }

            double total = Math.Max(pool.Count, 1);
            HashSet<string> seedGenres = new HashSet<string>(seed.Genres, StringComparer.OrdinalIgnoreCase);
            HashSet<string> seedTags = new HashSet<string>(seed.Tags, StringComparer.OrdinalIgnoreCase);

            List<(BaseItem Item, double Score)> scored = new List<(BaseItem, double)>();
            List<BaseItem> fallback = new List<BaseItem>();

            foreach (BaseItem item in pool)
            {
                if (hideWatched && SafeIsPlayed(item, user))
                {
                    continue;
                }

                double score = 0;
                foreach (string g in item.Genres)
                {
                    if (seedGenres.Contains(g) && genreFreq.TryGetValue(g, out int f))
                    {
                        // 0.1 floor so even a library-wide genre still counts a little.
                        score += 0.1 + Math.Log(total / f);
                    }
                }

                foreach (string t in item.Tags)
                {
                    if (seedTags.Contains(t) && tagFreq.TryGetValue(t, out int f))
                    {
                        // Tags are the sub-categories (slasher, heist, satire...) and the
                        // strongest similarity signal: rarity-weighted AND boosted 1.5x
                        // so subcategory overlap outranks broad genre overlap.
                        score += 1.5 * (0.1 + Math.Log(total / f));
                    }
                }

                if (score > 0)
                {
                    scored.Add((item, score));
                }
                else
                {
                    fallback.Add(item);
                }
            }

            List<BaseItem> result = new List<BaseItem>();
            HashSet<string> seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((BaseItem item, double _) in scored
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Item.CommunityRating ?? 0))
            {
                if (result.Count >= maxItems)
                {
                    break;
                }

                // Collapse duplicate copies of the same movie.
                string key = $"{item.Name}|{item.ProductionYear}";
                if (seenTitles.Add(key))
                {
                    result.Add(item);
                }
            }

            // Backfill with the best-rated remaining picks so a row is never one weak entry.
            if (result.Count < Math.Max(config.MinItemsPerRow, 1))
            {
                foreach (BaseItem item in fallback.OrderByDescending(i => i.CommunityRating ?? 0))
                {
                    if (result.Count >= Math.Max(config.MinItemsPerRow, 1))
                    {
                        break;
                    }

                    string key = $"{item.Name}|{item.ProductionYear}";
                    if (seenTitles.Add(key))
                    {
                        result.Add(item);
                    }
                }
            }

            return result;
        }

        private static bool SafeIsPlayed(BaseItem item, User user)
        {
            try
            {
                return item.IsPlayed(user, null!);
            }
            catch (Exception)
            {
                // If user data can't be read, treat as unwatched rather than dropping it.
                return false;
            }
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
                IReadOnlyList<BaseItem> similar = GetSimilarTo(user, seeds[s], config);
                for (int rank = 0; rank < similar.Count; rank++)
                {
                    BaseItem item = similar[rank];
                    if (seedIds.Contains(item.Id))
                    {
                        continue;
                    }

                    double add = seedWeight * (1.0 / (rank + 1));
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

            return scores
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => pool[kv.Key].CommunityRating ?? 0)
                .Select(kv => pool[kv.Key])
                .Take(maxItems)
                .ToList();
        }
    }
}
