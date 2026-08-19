using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// Jellyfin's register of open live streams, holding whatever a test put in it.
/// </summary>
/// <remarks>
/// The middleware asks it one question, so this answers one question. Everything else throws
/// rather than returning a plausible nothing, so a test that starts depending on more says so.
/// </remarks>
internal sealed class FakeMediaSourceManager : IMediaSourceManager
{
    private readonly IReadOnlyDictionary<string, ILiveStream> _streams;

    public FakeMediaSourceManager(IReadOnlyDictionary<string, ILiveStream> streams)
    {
        _streams = streams;
    }

    public ILiveStream GetLiveStreamInfo(string id) => _streams.GetValueOrDefault(id)!;

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => throw new NotSupportedException();

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => throw new NotSupportedException();

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => throw new NotSupportedException();

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => throw new NotSupportedException();

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => throw new NotSupportedException();

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User user = null!) => throw new NotSupportedException();

    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(
        string id,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CloseLiveStream(string id) => throw new NotSupportedException();

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public bool SupportsDirectStream(string path, MediaProtocol protocol) => throw new NotSupportedException();

    public MediaProtocol GetPathProtocol(string path) => throw new NotSupportedException();

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
        => throw new NotSupportedException();

    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
