using System;
using System.Globalization;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Api;
using TVHeadEnd.Compatibility.Jellyfin12;
using TVHeadEnd.Core.Media;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// The media sources a recording is published with, and the addresses they carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes of the same recording: the placeholder a listing carries, which promises nothing
    /// and exists so that a listing never has to analyse what it lists, and the described source
    /// playback negotiation is answered with. They share one identifier, which is the recording
    /// item's own -- clients ask for a media source by the item identifier when they hold no source
    /// of their own, and a recording identified any other way is one they cannot name.
    /// </para>
    /// <para>
    /// The split between what the client is told and what the server uses is deliberate and is the
    /// same one live TV makes: a whole seekable file to the client, an HTTP address to Jellyfin.
    /// </para>
    /// </remarks>
    public sealed class RecordingMediaSourceFactory
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IServerApplicationHost _applicationHost;
        private readonly TvheadendAccessSecret _secret;
        private readonly ILogger<RecordingMediaSourceFactory> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingMediaSourceFactory"/> class.
        /// </summary>
        /// <param name="libraryManager">Jellyfin's library, which owns the item identifier.</param>
        /// <param name="applicationHost">The Jellyfin application host, for this server's address.</param>
        /// <param name="secret">The secret a published address is signed with.</param>
        /// <param name="logger">The logger.</param>
        public RecordingMediaSourceFactory(
            ILibraryManager libraryManager,
            IServerApplicationHost applicationHost,
            TvheadendAccessSecret secret,
            ILogger<RecordingMediaSourceFactory> logger)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(applicationHost);
            ArgumentNullException.ThrowIfNull(secret);

            _libraryManager = libraryManager;
            _applicationHost = applicationHost;
            _secret = secret;
            _logger = logger;
        }

        /// <summary>
        /// The placeholder a listing carries for one recording.
        /// </summary>
        /// <param name="recording">The recording.</param>
        /// <returns>The placeholder source.</returns>
        public MediaSourceInfo PlaceholderFor(MyRecordingInfo recording)
            => BuildPlaceholderSource(MediaSourceIdFor(recording));

        /// <summary>
        /// The source playback negotiation is answered with.
        /// </summary>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <param name="recording">The recording, where the server still lists it.</param>
        /// <returns>The media source.</returns>
        public MediaSourceInfo SourceFor(string id, MyRecordingInfo? recording)
            => BuildRecordingSource(
                id,
                recording is not null
                    ? MediaSourceIdFor(recording)
                    : Playback.TvheadendItems.RecordingItemId(_libraryManager, id, typeof(MediaBrowser.Controller.Entities.Video))
                        .ToString("N", CultureInfo.InvariantCulture),
                BuildRecordingUrl(id));

        /// <summary>
        /// The identifier one recording's media source is addressed by.
        /// </summary>
        /// <param name="recording">The recording.</param>
        /// <returns>The identifier.</returns>
        public string MediaSourceIdFor(MyRecordingInfo recording)
            => RecordingMediaSourceId(_libraryManager, recording);

        /// <summary>
        /// The identifier a client sends back as MediaSourceId: the recording item's own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Jellyfin gives an ordinary library item a media source whose identifier <em>is</em> the
        /// item's -- <c>BaseItem.GetVersionInfo</c> writes <c>item.Id.ToString("N")</c> -- and
        /// clients are built on that. The native Android app, asked to play something it holds no
        /// media source for, sends the item identifier as the media source identifier; the server
        /// then keeps only the source that matches it. Measured: with any other identifier the
        /// response carries no sources and no play session, the app's resolver fails, and the
        /// screen stays black with no error anywhere.
        /// </para>
        /// <para>
        /// It also has to be readable as a GUID, which an item identifier is. Two places
        /// downstream parse it as one -- <c>DynamicHlsHelper.GetMasterPlaylistInternal</c>
        /// unconditionally, and <c>StreamingHelpers.GetStreamingState</c> when its lookup finds
        /// nothing. And it is the one GUID a saved source may carry:
        /// <c>MediaSourceManager.GetStaticMediaSources</c> discards a saved source whose
        /// identifier parses as a GUID unless it is the item's own, so the placeholder can share
        /// it rather than needing a second identity.
        /// </para>
        /// </remarks>
        /// <param name="libraryManager">Jellyfin's library, which owns the derivation.</param>
        /// <param name="recording">The recording, which decides the item type the identifier is derived with.</param>
        /// <returns>The media source identifier.</returns>
        public static string RecordingMediaSourceId(ILibraryManager libraryManager, MyRecordingInfo recording)
        {
            ArgumentNullException.ThrowIfNull(recording);

            return Playback.TvheadendItems.RecordingItemId(
                    libraryManager,
                    recording.Id ?? string.Empty,
                    Playback.TvheadendItems.RecordingItemType(RecordingItemMapper.MediaTypeFor(recording.ChannelType), RecordingItemMapper.ContentTypeFor(recording)))
                .ToString("N", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The source a listing reports: a placeholder, standing for a recording nobody has asked
        /// to play yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It carries no streams, because the listing does not analyse and must not guess.
        /// </para>
        /// <para>
        /// Its identifier is the recording item's own GUID -- the same value the described source
        /// carries, see <see cref="RecordingMediaSourceId"/>. Sharing it is what ties a listed
        /// recording to the source playback negotiation answers with.
        /// </para>
        /// </remarks>
        /// <param name="mediaSourceId">The recording item's identifier.</param>
        /// <returns>The placeholder source.</returns>
        public static MediaSourceInfo BuildPlaceholderSource(string mediaSourceId)
        {
            ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);

            return new MediaSourceInfo
            {
                Id = mediaSourceId,
                Type = MediaSourceType.Placeholder,
                Protocol = MediaProtocol.Http,

                // The starting assumption, which the analysis replaces with whatever the
                // recording turns out to be. Written under the one name this plugin gives
                // MPEG-TS rather than spelled out, so it cannot drift from the live path.
                Container = JellyfinContainerNames.TransportStream,
                MediaStreams = [],
            };
        }

        /// <summary>
        /// The described source of one recording, as the client and the server each need it.
        /// </summary>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <param name="mediaSourceId">The recording item's identifier, which the source is addressed by.</param>
        /// <param name="url">The address this plugin serves the recording from.</param>
        /// <returns>The source.</returns>
        public static MediaSourceInfo BuildRecordingSource(string id, string mediaSourceId, string url)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);

            return new MediaSourceInfo
            {
                Path = VirtualRecordingPath(id),
                Protocol = MediaProtocol.File,
                EncoderPath = url,
                EncoderProtocol = MediaProtocol.Http,
                Id = mediaSourceId,

                // Replaced by whatever the sample turns out to be. TVHeadend's DVR profile
                // decides the container, and a server on one of the WebTV profiles writes
                // Matroska, so this is a starting point rather than a claim.
                Container = JellyfinContainerNames.TransportStream,
                AnalyzeDurationMs = 2000,
                MediaStreams = [],
            };
        }

        /// <summary>
        /// The name a recording carries as a file, for a file nobody opens.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The recording lives on the TVHeadend server, so there is no local file to name and
        /// none is invented. Nothing on this server reads it: <c>MediaSourceInfo.Path</c> is a
        /// plain property, <c>StreamBuilder</c> never looks at it, and the one place that would
        /// have -- <c>AttachMediaSourceInfo</c> -- takes <c>EncoderPath</c> instead. It exists so
        /// that a source claiming to be a file says which file it means, in logs and in a
        /// playback report.
        /// </para>
        /// <para>
        /// Deliberately not shaped like a real path. A client configured for direct file access
        /// resolves what it is given against its own filesystem, and a plausible-looking path is
        /// exactly the one that could accidentally resolve to something else.
        /// </para>
        /// </remarks>
        private static string VirtualRecordingPath(string id)
            => "TVHeadend/Recordings/" + id;

        /// <summary>
        /// Builds the address Jellyfin fetches a recording from: this plugin's own endpoint, not
        /// TVHeadend's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TVHeadend drops the connection when FFmpeg seeks back to the start after analysing the
        /// stream, and Jellyfin has no way to tell FFmpeg not to. Serving the recording here
        /// turns every seek into a fresh request upstream, which TVHeadend answers reliably, and
        /// puts recordings where live TV already is.
        /// </para>
        /// <para>
        /// The address says nothing about the container, because at the point it is built nothing
        /// knows it: TVHeadend's DVR profile decides that, and the answer arrives with the
        /// analysis. The old <c>stream.ts</c> spelling claimed MPEG-TS of every recording,
        /// including the Matroska a WebTV profile writes.
        /// </para>
        /// </remarks>
        private string BuildRecordingUrl(string id)
        {
            try
            {
                var secret = _secret.Ensure();
                return _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
                    + Api.TvHeadendRecordingsController.StreamPathFor(Api.TvheadendAccessToken.Create(id, secret));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TVHeadend: no playback address could be built for recording {RecordingId}", id);
                return string.Empty;
            }
        }
    }
}
