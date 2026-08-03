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
    /// The brain. Ranks candidates for "Because You Watched X" the way a person who knows
    /// film would, using four signals in order of strength:
    ///
    ///  1. CONTENT TAGS (the subcategories: hood, gang, slasher, satire, simulation...)
    ///     rarity-weighted and boosted; mood adjectives and metadata debris are filtered
    ///     out first because "calm" and "playful" are feelings, not similarity.
    ///  2. PEOPLE: a shared director or writer is a strong tie (the Hughes Brothers link
    ///     Menace II Society to Dead Presidents; the Wachowskis link The Matrix to
    ///     The Animatrix and V for Vendetta).
    ///  3. ERA: movies from the same wave (early-90s hood cinema, late-90s reality sci-fi)
    ///     get a modest proximity bonus.
    ///  4. GENRES: weak background evidence for RANKING, but a hard GATE for eligibility:
    ///     a candidate sharing zero genres with the seed is out entirely — tone first,
    ///     theme second ("a comedy about making a movie is not a rec for a drama about
    ///     making a movie").
    ///
    /// Rating only breaks ties. Watched items excluded. Duplicate copies collapsed.
    /// All scoring happens in code; Jellyfin's query-level genre/tag filters are not
    /// trusted (their semantics are inconsistent on 10.11).
    /// </summary>
    public class RecommendationEngine
    {
        private const int PoolLimit = 5000;
        private const int PeopleStageCandidates = 64;

        private const double TagBoost = 1.5;
        private const double GenreDemotion = 0.5;
        private const double SharedDirectorBonus = 4.0;
        private const double SharedWriterBonus = 3.0;

        /// <summary>
        /// Mood adjectives and metadata debris that must never count as similarity.
        /// Users can extend this via the IgnoredTags plugin setting.
        /// </summary>
        private static readonly HashSet<string> JunkTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "admiring", "adoring", "affectation", "aggressive", "ambivalent", "amused",
            "angry", "antagonistic", "anxious", "approving", "assertive", "audacious",
            "bitter", "bold", "calm", "candid", "celebratory", "cheerful", "complicated",
            "critical", "desperate", "detached", "dramatic", "eerie", "empathetic",
            "excited", "exuberant", "frantic", "frightened", "gentle", "grim", "happy",
            "hopeful", "inflammatory", "intense", "joyful", "melancholic", "moody",
            "playful", "provocative", "relaxed", "sad", "sardonic", "scary", "shocking",
            "suspenseful", "sympathetic", "tense", "uplifting", "vexed",
            "duringcreditsstinger", "aftercreditsstinger"
        };

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
            HashSet<string> ignored = BuildIgnoredTags(config);

            IReadOnlyList<BaseItem> pool = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                EnableTotalRecordCount = false,
                EnableGroupByMetadataKey = true,
                ExcludeItemIds = new[] { seed.Id },
                Limit = PoolLimit
            });

            // Library-wide rarity census (junk tags excluded entirely).
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
                    if (!ignored.Contains(t))
                    {
                        tagFreq[t] = tagFreq.TryGetValue(t, out int c) ? c + 1 : 1;
                    }
                }
            }

            double total = Math.Max(pool.Count, 1);
            HashSet<string> seedGenres = new HashSet<string>(seed.Genres, StringComparer.OrdinalIgnoreCase);
            HashSet<string> seedTags = new HashSet<string>(
                seed.Tags.Where(t => !ignored.Contains(t)), StringComparer.OrdinalIgnoreCase);
            int? seedYear = seed.ProductionYear;

            List<(BaseItem Item, double Score)> scored = new List<(BaseItem, double)>();
            List<BaseItem> fallback = new List<BaseItem>();

            string seedKey = $"{seed.Name}|{seed.ProductionYear}";

            foreach (BaseItem item in pool)
            {
                if (hideWatched && SafeIsPlayed(item, user))
                {
                    continue;
                }

                // A duplicate copy of the seed itself must never appear in its own row.
                if (string.Equals($"{item.Name}|{item.ProductionYear}", seedKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // TONE GATE: theme only counts between movies that agree on what KIND of
                // movie they are. Zero shared genres = ineligible, no matter how many
                // thematic tags overlap (a comedy about making a movie is not a rec for
                // a drama about making a movie).
                if (!item.Genres.Any(g => seedGenres.Contains(g)))
                {
                    fallback.Add(item);
                    continue;
                }

                double score = 0;

                // Signal 1: content tags (subcategories) — strongest metadata signal.
                foreach (string t in item.Tags)
                {
                    if (seedTags.Contains(t) && tagFreq.TryGetValue(t, out int f))
                    {
                        score += TagBoost * (0.1 + Math.Log(total / f));
                    }
                }

                // Signal 4: genres — weak background evidence, demoted.
                foreach (string g in item.Genres)
                {
                    if (seedGenres.Contains(g) && genreFreq.TryGetValue(g, out int f))
                    {
                        score += GenreDemotion * (0.1 + Math.Log(total / f));
                    }
                }

                // Signal 3: era proximity — only when there's already a real connection.
                if (score > 0 && seedYear.HasValue && item.ProductionYear.HasValue)
                {
                    int diff = Math.Abs(seedYear.Value - item.ProductionYear.Value);
                    if (diff <= 3)
                    {
                        score += 1.5;
                    }
                    else if (diff <= 7)
                    {
                        score += 1.0;
                    }
                    else if (diff <= 12)
                    {
                        score += 0.5;
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

            // Signal 2: people. Shared directors/writers are a strong tie. Only computed
            // for the top candidates to keep this cheap on big libraries.
            (HashSet<string> SeedDirectors, HashSet<string> SeedWriters) seedPeople = GetKeyPeople(seed);
            if (seedPeople.SeedDirectors.Count > 0 || seedPeople.SeedWriters.Count > 0)
            {
                List<(BaseItem Item, double Score)> top = scored
                    .OrderByDescending(x => x.Score)
                    .Take(PeopleStageCandidates)
                    .ToList();

                Dictionary<Guid, double> bonuses = new Dictionary<Guid, double>();
                foreach ((BaseItem item, double _) in top)
                {
                    (HashSet<string> dirs, HashSet<string> writers) = GetKeyPeople(item);
                    double bonus = dirs.Count(d => seedPeople.SeedDirectors.Contains(d)) * SharedDirectorBonus
                                 + writers.Count(w => seedPeople.SeedWriters.Contains(w)) * SharedWriterBonus;
                    if (bonus > 0)
                    {
                        bonuses[item.Id] = bonus;
                    }
                }

                if (bonuses.Count > 0)
                {
                    scored = scored
                        .Select(x => bonuses.TryGetValue(x.Item.Id, out double b) ? (x.Item, x.Score + b) : x)
                        .ToList();
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

                string key = $"{item.Name}|{item.ProductionYear}";
                if (seenTitles.Add(key))
                {
                    result.Add(item);
                }
            }

            // Backfill with best-rated remaining picks so a row is never one weak entry.
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

        private static HashSet<string> BuildIgnoredTags(PluginConfiguration config)
        {
            HashSet<string> ignored = new HashSet<string>(JunkTags, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(config.IgnoredTags))
            {
                foreach (string t in config.IgnoredTags.Split(','))
                {
                    string trimmed = t.Trim();
                    if (trimmed.Length > 0)
                    {
                        ignored.Add(trimmed);
                    }
                }
            }

            return ignored;
        }

        private (HashSet<string> SeedDirectors, HashSet<string> SeedWriters) GetKeyPeople(BaseItem item)
        {
            HashSet<string> directors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> writers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (PersonInfo p in _libraryManager.GetPeople(item))
                {
                    if (string.IsNullOrWhiteSpace(p.Name))
                    {
                        continue;
                    }

                    if (p.Type == PersonKind.Director)
                    {
                        directors.Add(p.Name);
                    }
                    else if (p.Type == PersonKind.Writer)
                    {
                        writers.Add(p.Name);
                    }
                }
            }
            catch (Exception)
            {
                // People lookup failing must never break the row.
            }

            return (directors, writers);
        }

        private static bool SafeIsPlayed(BaseItem item, User user)
        {
            try
            {
                return item.IsPlayed(user, null!);
            }
            catch (Exception)
            {
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
