using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BecauseYouWatched.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BecauseYouWatched.Startup
{
    /// <summary>
    /// On startup, finds the Home Screen Sections plugin (if installed) and registers our
    /// "Because You Watched" section through its reflection-based PluginInterface.RegisterSection.
    /// No compile-time dependency on that plugin: everything is done through its own types so
    /// the plugin-isolation load context is respected.
    /// </summary>
    public sealed class HomeSectionsRegistrar : IHostedService
    {
        private const string HomeSectionsAssemblyMarker = ".HomeScreenSections";
        private const string PluginInterfaceTypeName = "Jellyfin.Plugin.HomeScreenSections.PluginInterface";
        private const int MaxAttempts = 30;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

        private readonly ILogger<HomeSectionsRegistrar> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="HomeSectionsRegistrar"/> class.
        /// </summary>
        public HomeSectionsRegistrar(ILogger<HomeSectionsRegistrar> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire-and-forget so we don't block host startup; Home Screen Sections may load slightly after us.
            _ = Task.Run(() => RegisterWithRetryAsync(cancellationToken), cancellationToken);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task RegisterWithRetryAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= MaxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    if (TryRegister())
                    {
                        _logger.LogInformation("Because You Watched: section registered with Home Screen Sections.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Because You Watched: registration attempt {Attempt} failed, retrying.", attempt);
                }

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "Because You Watched: could not find the Home Screen Sections plugin after {Attempts} attempts. "
                + "Install IAmParadox27's Home Screen Sections plugin for the row to appear.",
                MaxAttempts);
        }

        private bool TryRegister()
        {
            Assembly? homeSections = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(HomeSectionsAssemblyMarker, StringComparison.Ordinal) ?? false);

            if (homeSections is null)
            {
                return false;
            }

            Type? pluginInterface = homeSections.GetType(PluginInterfaceTypeName);
            MethodInfo? registerSection = pluginInterface?.GetMethod("RegisterSection", BindingFlags.Public | BindingFlags.Static);
            if (registerSection is null)
            {
                return false;
            }

            // The single parameter is Newtonsoft's JObject as the host plugin sees it.
            // Parse our JSON with THAT type so the load contexts line up.
            Type payloadType = registerSection.GetParameters()[0].ParameterType;
            MethodInfo? parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            if (parse is null)
            {
                return false;
            }

            object? payload = parse.Invoke(null, new object[] { BuildPayloadJson() });
            if (payload is null)
            {
                return false;
            }

            registerSection.Invoke(null, new[] { payload });
            return true;
        }

        private static string BuildPayloadJson()
        {
            PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            Dictionary<string, object?> payload = new Dictionary<string, object?>
            {
                ["id"] = "BecauseYouWatched",
                ["displayText"] = config.RowTitle,
                ["limit"] = 1,
                ["route"] = "movies",
                ["additionalData"] = "movies",
                ["resultsAssembly"] = typeof(BecauseYouWatchedResults).Assembly.FullName,
                ["resultsClass"] = typeof(BecauseYouWatchedResults).FullName,
                ["resultsMethod"] = nameof(BecauseYouWatchedResults.GetResults)
            };

            return JsonSerializer.Serialize(payload);
        }
    }
}
