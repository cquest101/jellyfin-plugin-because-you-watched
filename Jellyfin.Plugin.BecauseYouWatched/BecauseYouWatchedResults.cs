using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.BecauseYouWatched.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BecauseYouWatched
{
    /// <summary>
    /// Results provider for the Home Screen Sections rows. That plugin DI-constructs this
    /// class and calls <see cref="GetResults"/> via reflection, so it depends only on
    /// Jellyfin core services. When the section payload carries a movie id in
    /// AdditionalData (the per-movie "Because You Watched X" rows), results are similar to
    /// THAT movie; otherwise it falls back to the blended row.
    /// </summary>
    public class BecauseYouWatchedResults
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;
        private readonly ILogger<BecauseYouWatchedResults> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="BecauseYouWatchedResults"/> class.
        /// </summary>
        public BecauseYouWatchedResults(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IDtoService dtoService,
            ILogger<BecauseYouWatchedResults> logger)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _dtoService = dtoService;
            _logger = logger;
        }

        /// <summary>
        /// Invoked by the Home Screen Sections plugin. Returns the row's items.
        /// </summary>
        /// <param name="payload">The section payload (user id + optional seed movie id).</param>
        /// <returns>The recommendation row.</returns>
        public QueryResult<BaseItemDto> GetResults(SectionPayload payload)
        {
            User? user = _userManager.GetUserById(payload.UserId);
            if (user is null)
            {
                return new QueryResult<BaseItemDto>();
            }

            PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            RecommendationEngine engine = new RecommendationEngine(_libraryManager, _logger);

            IReadOnlyList<BaseItem> items;
            if (Guid.TryParse(payload.AdditionalData, out Guid seedId)
                && _libraryManager.GetItemById(seedId) is BaseItem seed)
            {
                items = engine.GetSimilarTo(user, seed, config);
            }
            else
            {
                items = engine.GetRecommendations(user, config);
            }

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

            BaseItemDto[] dtos = items
                .Select(i => _dtoService.GetBaseItemDto(i, dtoOptions, user))
                .ToArray();

            return new QueryResult<BaseItemDto>(dtos);
        }
    }
}
