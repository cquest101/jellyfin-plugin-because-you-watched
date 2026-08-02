using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.BecauseYouWatched.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BecauseYouWatched.Startup
{
    /// <summary>
    /// Standalone mode: with NO other plugins installed, this scheduled task builds a
    /// per-user "Because You Watched" playlist from the same brain, so the plugin is useful
    /// on its own. Jellyfin auto-discovers IScheduledTask implementations, so no registration
    /// is needed. The Home Screen Sections row is the premium path on top of this.
    /// </summary>
    public class RebuildPlaylistsTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IPlaylistManager _playlistManager;
        private readonly ILogger<RebuildPlaylistsTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RebuildPlaylistsTask"/> class.
        /// </summary>
        public RebuildPlaylistsTask(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            ILogger<RebuildPlaylistsTask> logger)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Because You Watched: rebuild playlists";

        /// <inheritdoc />
        public string Key => "BecauseYouWatchedRebuildPlaylists";

        /// <inheritdoc />
        public string Description => "Rebuilds each user's standalone 'Because You Watched' playlist from their recent watches.";

        /// <inheritdoc />
        public string Category => "Library";

        /// <inheritdoc />
        public bool IsHidden => false;

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        public bool IsLogged => true;

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(12).Ticks
                }
            };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.EnableStandalonePlaylist)
            {
                progress.Report(100);
                return;
            }

            string title = string.IsNullOrWhiteSpace(config.RowTitle) ? "Because You Watched" : config.RowTitle;
            RecommendationEngine engine = new RecommendationEngine(_libraryManager);

            List<User> users = _userManager.Users.ToList();
            for (int i = 0; i < users.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                User user = users[i];

                try
                {
                    IReadOnlyList<BaseItem> recs = engine.GetRecommendations(user, config);
                    if (recs.Count > 0)
                    {
                        await BuildOrRefreshAsync(user, title, recs, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Because You Watched: failed to build playlist for user {UserId}.", user.Id);
                }

                progress.Report((i + 1) * 100.0 / Math.Max(users.Count, 1));
            }
        }

        private async Task BuildOrRefreshAsync(User user, string title, IReadOnlyList<BaseItem> recs, CancellationToken cancellationToken)
        {
            Guid[] ids = recs.Select(r => r.Id).ToArray();

            Playlist? existing = _playlistManager.GetPlaylists(user.Id)
                .FirstOrDefault(p => string.Equals(p.Name, title, StringComparison.OrdinalIgnoreCase));

            Guid playlistId;
            if (existing is not null)
            {
                // Clear it so the refresh is a replace, not an append.
                existing.LinkedChildren = Array.Empty<LinkedChild>();
                await existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                playlistId = existing.Id;
            }
            else
            {
                PlaylistCreationResult result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = title,
                    UserId = user.Id
                }).ConfigureAwait(false);
                playlistId = Guid.Parse(result.Id);
            }

            await _playlistManager.AddToPlaylistAsync(playlistId, ids, user.Id).ConfigureAwait(false);
        }
    }
}
