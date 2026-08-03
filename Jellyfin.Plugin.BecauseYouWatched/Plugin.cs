using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.BecauseYouWatched.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BecauseYouWatched
{
    /// <summary>
    /// Because You Watched: replaces Jellyfin's broken "recommendations" row with real,
    /// similarity-scored picks built off what the user actually watched.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public override string Name => "Because You Watched";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("6c784ca1-2a6f-4132-af78-405582869670");

        /// <inheritdoc />
        public override string Description =>
            "One home screen row per movie you recently watched, filled with genuinely similar "
            + "titles from your own library. Four-signal scoring engine (rarity-weighted tags, "
            + "genres, shared directors/writers, era) with a tone gate and mood-tag filter. "
            + "Watched titles hidden, rows follow your history. Rows render through the Home "
            + "Screen Sections plugin; standalone playlist mode needs no other plugins.";

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}.Configuration.configPage.html",
                        GetType().Namespace)
                }
            };
        }
    }
}
