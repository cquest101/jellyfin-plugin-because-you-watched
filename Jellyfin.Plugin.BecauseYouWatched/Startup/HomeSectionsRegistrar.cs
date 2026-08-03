using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.BecauseYouWatched.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BecauseYouWatched.Startup
{
    /// <summary>
    /// Registers per-movie "Because You Watched X" rows with the Home Screen Sections
    /// plugin (if installed) through its reflection-based PluginInterface.RegisterSection,
    /// and re-registers periodically so the row titles follow the watch history. No
    /// compile-time dependency on that plugin.
    /// </summary>
    public sealed class HomeSectionsRegistrar : IHostedService
    {
        private const string HomeSectionsAssemblyMarker = ".HomeScreenSections";
        private const string PluginInterfaceTypeName = "Jellyfin.Plugin.HomeScreenSections.PluginInterface";
        private const int MaxFindAttempts = 30;
        private static readonly TimeSpan FindRetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

        private readonly ILogger<HomeSectionsRegistrar> _logger;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;

        private string _lastRegisteredState = string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="HomeSectionsRegistrar"/> class.
        /// </summary>
        public HomeSectionsRegistrar(
            ILogger<HomeSectionsRegistrar> logger,
            IUserManager userManager,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager)
        {
            _logger = logger;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire-and-forget so we don't block host startup; Home Screen Sections may load slightly after us.
            _ = Task.Run(() => RunAsync(cancellationToken), cancellationToken);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            MethodInfo? registerSection = null;
            MethodInfo? parse = null;

            for (int attempt = 1; attempt <= MaxFindAttempts && !cancellationToken.IsCancellationRequested; attempt++)
            {
                (registerSection, parse) = FindHomeSectionsApi();
                if (registerSection != null && parse != null)
                {
                    break;
                }

                await Task.Delay(FindRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            if (registerSection is null || parse is null)
            {
                _logger.LogWarning(
                    "Because You Watched: could not find the Home Screen Sections plugin after {Attempts} attempts. "
                    + "Home rows need IAmParadox27's Home Screen Sections plugin; the standalone playlist still works.",
                    MaxFindAttempts);
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    RegisterRows(registerSection, parse);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Because You Watched: registering rows failed; will retry.");
                }

                await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        private (MethodInfo? RegisterSection, MethodInfo? Parse) FindHomeSectionsApi()
        {
            Assembly? homeSections = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(HomeSectionsAssemblyMarker, StringComparison.Ordinal) ?? false);

            Type? pluginInterface = homeSections?.GetType(PluginInterfaceTypeName);
            MethodInfo? registerSection = pluginInterface?.GetMethod("RegisterSection", BindingFlags.Public | BindingFlags.Static);
            if (registerSection is null)
            {
                return (null, null);
            }

            // The single parameter is Newtonsoft's JObject as the host plugin sees it.
            // Parse our JSON with THAT type so the load contexts line up.
            Type payloadType = registerSection.GetParameters()[0].ParameterType;
            MethodInfo? parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });

            return (registerSection, parse);
        }

        private void RegisterRows(MethodInfo registerSection, MethodInfo parse)
        {
            PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            User? user = ResolvePrimaryUser(config);
            if (user is null)
            {
                return;
            }

            RecommendationEngine engine = new RecommendationEngine(_libraryManager);
            IReadOnlyList<BaseItem> seeds = engine.GetRecentSeeds(user, Math.Max(1, config.SeedCount));
            if (seeds.Count == 0)
            {
                return;
            }

            // Skip re-registering when nothing changed.
            string state = string.Join("|", seeds.Select(s => s.Id));
            if (state == _lastRegisteredState)
            {
                return;
            }

            for (int i = 0; i < seeds.Count; i++)
            {
                BaseItem seed = seeds[i];

                // NBSPs in the title so Home Screen Sections' translator (which matches
                // display text with spaces stripped) can't stamp its own "{0}" template on it.
                string title = ("Because You Watched " + seed.Name).Replace(' ', '\u00A0');

                Dictionary<string, object?> payload = new Dictionary<string, object?>
                {
                    ["id"] = $"BecauseYouWatchedSeed{i + 1}",
                    ["displayText"] = title,
                    ["limit"] = 1,
                    ["route"] = "movies",
                    ["additionalData"] = seed.Id.ToString(),
                    ["resultsAssembly"] = typeof(BecauseYouWatchedResults).Assembly.FullName,
                    ["resultsClass"] = typeof(BecauseYouWatchedResults).FullName,
                    ["resultsMethod"] = nameof(BecauseYouWatchedResults.GetResults)
                };

                object? parsed = parse.Invoke(null, new object[] { JsonSerializer.Serialize(payload) });
                if (parsed != null)
                {
                    registerSection.Invoke(null, new[] { parsed });
                }
            }

            _lastRegisteredState = state;
            _logger.LogInformation(
                "Because You Watched: registered {Count} per-movie rows for {User}: {Titles}",
                seeds.Count,
                user.Username,
                string.Join(", ", seeds.Select(s => s.Name)));
        }

        private User? ResolvePrimaryUser(PluginConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(config.PrimaryUserName))
            {
                return _userManager.GetUserByName(config.PrimaryUserName);
            }

            // Auto: the user with the most recent movie play.
            User? best = null;
            DateTime bestPlayed = DateTime.MinValue;
            RecommendationEngine engine = new RecommendationEngine(_libraryManager);

            foreach (User user in _userManager.GetUsers())
            {
                IReadOnlyList<BaseItem> recent = engine.GetRecentSeeds(user, 1);
                if (recent.Count == 0)
                {
                    continue;
                }

                DateTime? played = _userDataManager.GetUserData(user, recent[0])?.LastPlayedDate;
                if (played.HasValue && played.Value > bestPlayed)
                {
                    bestPlayed = played.Value;
                    best = user;
                }
            }

            return best;
        }
    }
}
