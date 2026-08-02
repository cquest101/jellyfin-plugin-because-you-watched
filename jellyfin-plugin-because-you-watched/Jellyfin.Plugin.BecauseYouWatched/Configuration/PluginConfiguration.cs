using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BecauseYouWatched.Configuration
{
    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
        /// </summary>
        public PluginConfiguration()
        {
            RowTitle = "Because You Watched";
            MaxItems = 16;
            SeedCount = 3;
            HideWatched = true;
            MinItemsPerRow = 5;
        }

        /// <summary>
        /// Gets or sets the title shown on the home row.
        /// </summary>
        public string RowTitle { get; set; }

        /// <summary>
        /// Gets or sets how many items the row shows.
        /// </summary>
        public int MaxItems { get; set; }

        /// <summary>
        /// Gets or sets how many recent watches to blend as seeds (1 = classic single-seed).
        /// </summary>
        public int SeedCount { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether already-watched items are hidden from the row.
        /// </summary>
        public bool HideWatched { get; set; }

        /// <summary>
        /// Gets or sets the floor below which the row backfills with same-genre picks
        /// so it is never a single weak result.
        /// </summary>
        public int MinItemsPerRow { get; set; }
    }
}
