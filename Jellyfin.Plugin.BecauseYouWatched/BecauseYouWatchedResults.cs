using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Entities;
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
    /// Results provider for the Home Screen Sections rail. That plugin DI-constructs this
    /// class and calls <see cref="GetResults"/> via reflection, so it depends only on
    /// Jellyfin core services.
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

            IReadOnlyList<BaseItem> items = new RecommendationEngine(_libraryManager).GetRecommendations(user, config);

            BaseItemDto[] dtos = items
                .Select(i => _dtoService.GetBaseItemDto(i, dtoOptions, user))
                .ToArray();

            return new QueryResult<BaseItemDto>(dtos);
        }
    }
}
