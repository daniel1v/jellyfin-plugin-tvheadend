using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// What a running stream tells Jellyfin it offers, asked more than once.
/// </summary>
/// <remarks>
/// The failure this guards was measured on air: ZDF resolved to direct play twice and to
/// <c>DirectPlayError</c> on the third question about the same open stream, 160 ms later, with
/// nothing about the broadcast changed. Jellyfin writes each request's outcome into the media
/// source it was given, and the plugin hands out one that lives as long as the stream, so one
/// viewer's transcoding verdict stayed behind and answered for everybody after them.
/// </remarks>
public class LiveStreamOfferTests
{
    [Fact]
    public void AnOfferOverwrittenByOneRequestIsRestoredForTheNext()
    {
        var stream = LiveStream(requiresVideoReencode: false);
        Assert.True(stream.MediaSource.SupportsDirectPlay);

        // Jellyfin, having chosen to transcode for one client, writing that back.
        stream.MediaSource.SupportsDirectPlay = false;
        stream.MediaSource.SupportsDirectStream = false;

        Assert.True(stream.MediaSource.SupportsDirectPlay);
        Assert.True(stream.MediaSource.SupportsDirectStream);
    }

    [Fact]
    public void AStreamThatMustBeReencodedIsNotTalkedIntoDirectPlay()
    {
        // The same restoration in the other direction: a leftover yes is as wrong as a leftover no.
        var stream = LiveStream(requiresVideoReencode: true);

        stream.MediaSource.SupportsDirectPlay = true;
        stream.MediaSource.SupportsDirectStream = true;

        Assert.False(stream.MediaSource.SupportsDirectPlay);
        Assert.False(stream.MediaSource.SupportsDirectStream);
    }

    [Fact]
    public void EverythingElseInTheDescriptionIsLeftAlone()
    {
        // Jellyfin fills the live stream identity in after opening and reads it back from here.
        var stream = LiveStream(requiresVideoReencode: false);

        stream.MediaSource.LiveStreamId = "written-by-jellyfin";
        stream.MediaSource.DefaultAudioStreamIndex = 5;

        Assert.Equal("written-by-jellyfin", stream.MediaSource.LiveStreamId);
        Assert.Equal(5, stream.MediaSource.DefaultAudioStreamIndex);
    }

    private static TvheadendLiveStream LiveStream(bool requiresVideoReencode)
    {
        var stream = new TvheadendLiveStream(
            "42",
            "ZDF",
            "http://tvheadend/stream",
            new Dictionary<string, string>(),
            new MediaSourceInfo(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            LiveStreamBuffer.MinimumSizeMegabytes,
            new UnusedClientFactory(),
            NullLogger.Instance);

        stream.RequiresVideoReencode = requiresVideoReencode;
        return stream;
    }

    private sealed class UnusedClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("These tests never reach TVHeadend.");
    }
}
