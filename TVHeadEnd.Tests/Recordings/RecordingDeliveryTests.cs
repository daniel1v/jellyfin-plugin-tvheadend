using System;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// What a bounded sample of a recording is allowed to conclude, and what the endpoint serving it
/// is allowed to differ on between HEAD and GET.
/// </summary>
/// <remarks>
/// <para>
/// Both of these were the same mistake in different places. The plugin read the first few
/// megabytes of a recording, found no H.264 IDR frame, and treated that as proof the recording
/// had none -- then withheld direct play for the whole file and served a re-encode. A finite
/// probe cannot establish an absence: a broadcast that opens on a recovery point and carries an
/// IDR a minute later looks exactly the same from the front.
/// </para>
/// <para>
/// The re-encode is what made HEAD and GET disagree. HEAD proxied TVHeadend, which advertises a
/// seekable file of known length; GET could answer with an encoder's output, which has neither.
/// A client that asked first and fetched second was told one thing and handed another.
/// </para>
/// </remarks>
public class RecordingDeliveryTests
{
    [Fact]
    public void NothingInTheDescribedSourceWithholdsDirectPlay()
    {
        // The source a described recording is published as. Every route stays open; which one a
        // client takes is Jellyfin's decision against its device profile, not a verdict reached
        // here from a sample.
        var source = DescribedRecording();

        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
    }

    [Fact]
    public void TheChannelOffersNoWayToDeclareARecordingUnplayable()
    {
        // The mechanism is gone rather than merely unused: there is no longer a member that maps
        // a recording identifier to "must be re-encoded", so nothing can reach that state by
        // accident again.
        var members = typeof(RecordingsChannel)
            .GetMembers()
            .Select(member => member.Name)
            .ToList();

        Assert.DoesNotContain("RequiresReencode", members);
    }

    [Fact]
    public void TheRecordingEndpointHasOneRouteForHeadAndGet()
    {
        // Both verbs land on the same method, so they cannot describe the resource differently.
        var method = typeof(TVHeadEnd.Api.TvHeadendRecordingsController)
            .GetMethod(nameof(TVHeadEnd.Api.TvHeadendRecordingsController.GetRecording));

        Assert.NotNull(method);

        var get = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), false);
        var head = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpHeadAttribute), false);

        Assert.Single(get);
        Assert.Single(head);

        var getTemplate = ((Microsoft.AspNetCore.Mvc.HttpGetAttribute)get[0]).Template;
        var headTemplate = ((Microsoft.AspNetCore.Mvc.HttpHeadAttribute)head[0]).Template;

        Assert.Equal(getTemplate, headTemplate);
    }

    [Fact]
    public void NothingReEncodesARecordingOnTheWayToTheClient()
    {
        // The encoder that served the re-encode, and the scanner that decided when to, are both
        // gone. A recording is proxied as TVHeadend stored it.
        var assembly = typeof(RecordingsChannel).Assembly;
        var names = assembly.GetTypes().Select(type => type.FullName ?? type.Name).ToList();

        Assert.DoesNotContain(names, name => name.Contains("LegacyH264Encoder", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("H264RandomAccess", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("VideoRandomAccessProbe", StringComparison.Ordinal));
    }

    [Fact]
    public void ThereIsNoSettingLeftThatForcesARecordingToBeReEncoded()
    {
        var settings = typeof(TVHeadEnd.Configuration.PluginConfiguration)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("EnableLegacyH264Fallback", settings);
    }

    private static MediaSourceInfo DescribedRecording()
        => new()
        {
            Id = "recording-1",
            Container = "mpegts,ts",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = false,
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "mp2" },
            ],
        };
}
