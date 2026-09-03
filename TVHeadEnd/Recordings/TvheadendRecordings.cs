using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Api;
using TVHeadEnd.Configuration;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Recordings;

/// <summary>
/// What TVHeadend holds, described as the recordings Jellyfin lists.
/// </summary>
/// <remarks>
/// <para>
/// The read side of recordings, and the only place a caller has to go through to get one finished.
/// A DVR entry on its own does not know whether its channel carries pictures, and it has neither
/// this server's address nor its secret -- so the channel kind and the artwork are filled in here,
/// where all three are known, rather than in the mapper that reads one HTSP message.
/// </para>
/// <para>
/// It sat in the live TV service, which meant the recordings channel had to hold the live TV
/// service to list a recording. Those are two different jobs and the coupling was the only thing
/// joining them.
/// </para>
/// </remarks>
public sealed class TvheadendRecordings
{
    private readonly TvheadendConnection _connection;
    private readonly TvheadendArtwork _artwork;
    private readonly IPluginPreferencesSource _preferences;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendRecordings"/> class.
    /// </summary>
    /// <param name="connection">The TVHeadend connection.</param>
    /// <param name="artwork">How an image reference becomes an address Jellyfin can fetch.</param>
    /// <param name="preferences">Whether a recording may borrow its channel's logo.</param>
    public TvheadendRecordings(
        TvheadendConnection connection,
        TvheadendArtwork artwork,
        IPluginPreferencesSource preferences)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(preferences);

        _connection = connection;
        _artwork = artwork;
        _preferences = preferences;
    }

    /// <summary>
    /// Gets a number that changes whenever TVHeadend's recordings do.
    /// </summary>
    /// <remarks>
    /// What the recordings channel keys its cache on, so that a listing is asked for again exactly
    /// when the server has something different to say and not otherwise.
    /// </remarks>
    public long Revision => _connection.Dvr.Revision;

    /// <summary>
    /// Gets every recording TVHeadend holds.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recordings, described and addressable.</returns>
    public async Task<IReadOnlyList<MyRecordingInfo>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _connection.WaitForInitialSyncAsync(cancellationToken).ConfigureAwait(false);

        var recordings = _connection.Dvr.GetEntries()
            .Where(JellyfinDvrMapper.IsRecording)
            .Select(JellyfinDvrMapper.ToRecording)
            .ToList();

        var endpoint = _connection.HttpEndpoint;
        var borrowLogos = _preferences.Current.UseChannelLogoWhereArtworkIsMissing;
        var typeForOther = _connection.Settings.ChannelTypeForOther;

        foreach (var recording in recordings)
        {
            var channel = _connection.Channels.Get(recording.ChannelId);

            // From the channel it was recorded from. The mapper reads one DVR entry, and a DVR
            // entry does not say whether its channel carries pictures -- so left unset it took the
            // enum's default, and every radio recording was published as video. The recordings
            // channel reads this to decide between an audio and a video item.
            recording.ChannelType = JellyfinChannelMapper.ChannelTypeFor(channel, typeForOther);

            // The channel's logo where the recording has no picture of its own, which with a
            // broadcast EPG is every recording: DVB EIT has no field for one. Published on the
            // padded address, because a logo handed over as it stands fills whatever frame
            // Jellyfin draws it in.
            var logo = borrowLogos ? channel?.Icon : null;

            recording.ImageUrl = _artwork.PaddedAddressFor(recording.ImageReference, logo, endpoint);
            recording.HasImage = !string.IsNullOrEmpty(recording.ImageUrl);
        }

        return recordings;
    }

    /// <summary>
    /// Finds one recording among the ones TVHeadend holds.
    /// </summary>
    /// <param name="recordingId">The TVHeadend recording identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recording, or <see langword="null"/> if the server no longer lists it.</returns>
    public async Task<MyRecordingInfo?> FindAsync(string recordingId, CancellationToken cancellationToken)
    {
        var recordings = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return recordings.FirstOrDefault(item => string.Equals(item.Id, recordingId, StringComparison.Ordinal));
    }
}
