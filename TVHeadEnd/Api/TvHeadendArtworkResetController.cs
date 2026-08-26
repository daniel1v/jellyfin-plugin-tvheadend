using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Playback;

namespace TVHeadEnd.Api
{
    /// <summary>
    /// Clears the artwork stored for this plugin's recordings so that it is fetched again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A recording is a channel item, and Jellyfin's channel manager gives one an image only when
    /// it has none: <c>if (!string.IsNullOrEmpty(info.ImageUrl) &amp;&amp;
    /// !item.HasImage(ImageType.Primary))</c>. So the first picture a recording is given is the
    /// one it keeps. When this plugin changes where artwork comes from -- or what shape it is
    /// published in -- the recordings somebody already has cannot follow, and nothing says so.
    /// </para>
    /// <para>
    /// Live channels and guide entries do not need this. Their images go through
    /// <c>GuideManager</c>, which compares the stored path against the new address and replaces it
    /// when they differ, so they correct themselves on the next refresh. This exists for the one
    /// path that cannot.
    /// </para>
    /// <para>
    /// A button rather than a sweep on every listing. It is needed after a change to the plugin
    /// and at no other time, and deleting images on a schedule is a great deal of authority for
    /// something that has to happen once.
    /// </para>
    /// </remarks>
    [ApiController]
    [Route("TVHeadend")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class TvHeadendArtworkResetController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger<TvHeadendArtworkResetController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvHeadendArtworkResetController"/> class.
        /// </summary>
        /// <param name="libraryManager">Jellyfin's library.</param>
        /// <param name="applicationPaths">Where Jellyfin keeps its cache.</param>
        /// <param name="logger">The logger.</param>
        public TvHeadendArtworkResetController(
            ILibraryManager libraryManager,
            IApplicationPaths applicationPaths,
            ILogger<TvHeadendArtworkResetController> logger)
        {
            _libraryManager = libraryManager;
            _applicationPaths = applicationPaths;
            _logger = logger;
        }

        /// <summary>
        /// Forgets the artwork stored for every recording this plugin provides.
        /// </summary>
        /// <remarks>
        /// Only the pictures, and only for items the channel manager recorded as belonging to this
        /// plugin's channel. Nothing else in the library is touched, and no recording is altered
        /// beyond losing an image it will be handed again.
        /// </remarks>
        /// <returns>How many recordings were cleared.</returns>
        [HttpPost("Artwork/Reset")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<ArtworkResetResult>> ResetRecordingArtwork()
        {
            // The identifier Jellyfin's own channel manager derived for this plugin's channel and
            // wrote onto every recording it stored. Asking the library by that is what keeps this
            // from reaching anything that is not ours.
            var channelId = TvheadendItems.RecordingsChannelId(_libraryManager);

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ChannelIds = [channelId],
                Recursive = true,
            });

            var cleared = 0;
            foreach (var item in items)
            {
                if (!item.HasImage(ImageType.Primary))
                {
                    continue;
                }

                try
                {
                    await item.DeleteImageAsync(ImageType.Primary, 0).ConfigureAwait(false);
                    cleared++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One recording that will not let go of its picture is not a reason to leave
                    // the rest holding theirs.
                    _logger.LogWarning(exception, "TVHeadend: could not clear the artwork of {Name}", item.Name);
                }
            }

            // Half a reset without this. Jellyfin caches a channel's listing on disk for three
            // hours, and while that cache stands it never asks the plugin, so nothing re-supplies
            // the picture that was just forgotten -- the recordings sit there with no image at all
            // until the cache ages out. Measured once: images cleared at 22:40, cache valid until
            // 01:20.
            DiscardCachedListing(channelId);

            _logger.LogInformation(
                "TVHeadend: cleared the artwork of {Cleared} of {Total} recordings; it is fetched again on the next listing",
                cleared,
                items.Count);

            return new ArtworkResetResult { Cleared = cleared, Total = items.Count };
        }

        /// <summary>
        /// Throws away the listing Jellyfin has cached for this plugin's channel.
        /// </summary>
        /// <remarks>
        /// There is no API for it: <c>IChannelManager</c> offers no way to say "ask again", and
        /// the cache is keyed by a path the channel manager builds privately. What is deleted is
        /// the directory belonging to this plugin's own channel and nothing else, and it is a
        /// cache, so the worst a mistake here can cost is one listing being rebuilt.
        /// </remarks>
        private void DiscardCachedListing(Guid channelId)
        {
            var cached = Path.Combine(
                _applicationPaths.CachePath,
                "channels",
                channelId.ToString("N", System.Globalization.CultureInfo.InvariantCulture));

            try
            {
                if (Directory.Exists(cached))
                {
                    Directory.Delete(cached, recursive: true);
                    _logger.LogInformation("TVHeadend: discarded the cached recordings listing so it is built again");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The pictures are still gone, and the listing will age out by itself within three
                // hours. Worth saying so, because until then the recordings have no image.
                _logger.LogWarning(
                    exception,
                    "TVHeadend: the cached recordings listing could not be discarded; artwork returns when it expires");
            }
        }
    }
}
