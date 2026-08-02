using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.BecauseYouWatched.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.BecauseYouWatched
{
    /// <summary>
    /// The brain. Builds a real "Because You Watched" row from the user's recent watches
    /// using Jellyfin's own similarity engine (SimilarTo, the engine behind /Items/Similar),
    /// blends several recent seeds, hides watched items, and backfills thin results.
    ///
    /// The Home Screen Sections plugin DI-constructs this class and calls GetResults via
    /// reflection, so the only dependencies are Jellyfin core services.
    /// </summary>
    public class BecauseYouWatchedResults
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;

        /// <summary>
        /// Initializes a new instance of the <see cref="BecauseYouWatchedResults"/> class.
        /// </summary>
        public BecauseYouWatchedResults(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IDtoService dtoService)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _dtoService = dtoService;
        }

        /// <summary>
        /// Invoked by the Home Screen Sections plugin. Returns the row's items.
        /// </summary>
        /// <param name="payload">The section payload (carries the user id).</param>
        /// <returns>The recommendation row.</returns>
        public QueryResult<BaseItemDto> GetResults(SectionPayload payload)
        {
            User? user = _userManager.GetUserById(payload.UserId);
            if (user is null)
            {
                return new QueryResult<BaseItemDto>();
            }

            PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            int maxItems = Math.Max(1, config.MaxItems);
            int seedCount = Math.Max(1, config.SeedCount);
            bool? excludeWatched = config.HideWatched ? false : (bool?)null;

            DtoOptions dtoOptions = new DtoOptions
            {
                Fields = new List<ItemFields>
                {
                    ItemFields.PrimaryImageAspectRatio,
                    ItemFields.Path
                },
                ImageTypeLimit = 1,
                ImageTypes = new List<ImageType>
                {
                    ImageType.Primary,
                    ImageType.Thumb,
                    ImageType.Backdrop
                }
            };

            // 1. The user's most recently played movies become the seeds.
            IReadOnlyList<BaseItem> seeds = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsPlayed = true,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                Limit = seedCount,
                Recursive = true,
                DtoOptions = dtoOptions
            });

            if (seeds.Count == 0)
            {
                return new QueryResult<BaseItemDto>();
            }

            HashSet<Guid> seedIds = seeds.Select(s => s.Id).ToHashSet();
            Dictionary<Guid, double> scores = new Dictionary<Guid, double>();
            Dictionary<Guid, BaseItem> pool = new Dictionary<Guid, BaseItem>();

            // 2. For each seed, pull Jellyfin's real similarity results and score by
            //    seed recency (more recent seed weighs more) and rank within that seed.
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
                    Recursive = true,
                    DtoOptions = dtoOptions
                });

                for (int p = 0; p < similar.Count; p++)
                {
                    BaseItem item = similar[p];
                    if (seedIds.Contains(item.Id))
                    {
                        continue;
                    }

                    double rankWeight = 1.0 / (p + 1);
                    double add = seedWeight * rankWeight;

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

            // 3. Backfill from shared genres if similarity came back thin, so a row is
            //    never a single weak pick (the gap the stock engine and other plugins leave).
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
                        Recursive = true,
                        DtoOptions = dtoOptions
                    });

                    foreach (BaseItem item in backfill)
                    {
                        if (seedIds.Contains(item.Id) || scores.ContainsKey(item.Id))
                        {
                            continue;
                        }

                        // Backfill sits below any real similarity hit.
                        scores[item.Id] = 0.0001;
                        pool[item.Id] = item;
                    }
                }
            }

            // 4. Rank, cap, and hand back DTOs.
            List<BaseItem> ordered = scores
                .OrderByDescending(kv => kv.Value)
                .Select(kv => pool[kv.Key])
                .Take(maxItems)
                .ToList();

            BaseItemDto[] dtos = ordered
                .Select(i => _dtoService.GetBaseItemDto(i, dtoOptions, user))
                .ToArray();

            return new QueryResult<BaseItemDto>(dtos);
        }
    }
}
