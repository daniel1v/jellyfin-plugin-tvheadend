using System;
using System.Linq;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using TVHeadEnd;
using Xunit;

namespace TVHeadEnd.Tests;

public class RecordingsChannelTests
{
    [Fact]
    public void TheChannelAnswersTheMediaInfoCallback()
    {
        // What a listing reports is a placeholder, so playback has to be able to ask for the
        // real description. Without this interface there is nothing to ask.
        Assert.True(typeof(IRequiresMediaInfoCallback).IsAssignableFrom(typeof(RecordingsChannel)));
    }

    [Fact]
    public void APlaceholderCarriesNoStreamsAtAll()
    {
        // Inventing them is worse than saying nothing: Jellyfin maps streams by their position
        // in this list, so made-up entries send FFmpeg's map arguments to the wrong tracks.
        var placeholder = RecordingsChannel.BuildPlaceholderSource("1312160563");

        Assert.Empty(placeholder.MediaStreams);
        Assert.Equal(MediaSourceType.Placeholder, placeholder.Type);
    }

    [Fact]
    public void APlaceholderIsNotMistakableForADescribedSource()
    {
        // The identifier a client comes back with has to be the described source. Keeping the
        // two textually distinct means a placeholder can never be taken for a description.
        var placeholder = RecordingsChannel.BuildPlaceholderSource("1312160563");
        var described = RecordingsChannel.RecordingMediaSourceId("1312160563");

        Assert.NotEqual(described, placeholder.Id);
        Assert.False(Guid.TryParse(placeholder.Id, out _));
    }

    [Fact]
    public void TheDescribedSourceIsIdentifiedByAGuid()
    {
        // DynamicHlsHelper.GetMasterPlaylistInternal parses this as a GUID unconditionally, and
        // StreamingHelpers.GetStreamingState does when its lookup finds nothing. Anything else
        // fails the request with "Unrecognized Guid format" before playback starts.
        Assert.True(Guid.TryParse(RecordingsChannel.RecordingMediaSourceId("1312160563"), out _));
    }

    [Fact]
    public void TheDescribedSourceKeepsItsIdentifierAcrossCalls()
    {
        // Jellyfin stores it and every later request carries it back, including after a restart.
        var first = RecordingsChannel.RecordingMediaSourceId("867835561");
        var second = RecordingsChannel.RecordingMediaSourceId("867835561");

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentRecordingsGetDifferentIdentifiers()
    {
        Assert.NotEqual(
            RecordingsChannel.RecordingMediaSourceId("867835561"),
            RecordingsChannel.RecordingMediaSourceId("962787396"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ARecordingWithoutAnIdentifierIsRefused(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => RecordingsChannel.RecordingMediaSourceId(id!));
        Assert.ThrowsAny<ArgumentException>(() => RecordingsChannel.BuildPlaceholderSource(id!));
    }

    [Fact]
    public void APlaceholderStatesTheContainerButPromisesNothingElse()
    {
        var placeholder = RecordingsChannel.BuildPlaceholderSource("1312160563");

        Assert.Equal("mpegts", placeholder.Container);
        Assert.Null(placeholder.Path);
        Assert.Null(placeholder.RunTimeTicks);
    }
}
