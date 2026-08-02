using System;

namespace Jellyfin.Plugin.BecauseYouWatched
{
    /// <summary>
    /// Payload the Home Screen Sections plugin hands to the results method.
    /// Shape mirrors that plugin's HomeScreenSectionPayload; Newtonsoft fills it by name.
    /// </summary>
    public class SectionPayload
    {
        /// <summary>
        /// Gets or sets the id of the user the row is being built for.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets any additional data passed by the host section.
        /// </summary>
        public string? AdditionalData { get; set; }
    }
}
