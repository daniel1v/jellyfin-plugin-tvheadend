using System;
using System.Linq;
using TVHeadEnd.Compatibility.Jellyfin12;
using TVHeadEnd.Core.Media;
using Xunit;

namespace TVHeadEnd.Tests.Compatibility;

/// <summary>
/// What Jellyfin calls the codecs a broadcast carries.
/// </summary>
/// <remarks>
/// These strings are a Jellyfin fact, and a wrong one is invisible: nothing errors, the device
/// profile simply matches nothing and a channel that could have been played direct is transcoded
/// instead. They used to live inside the transport stream parser, which meant a table reading DVB
/// descriptors was stating what a media player calls things.
/// </remarks>
public class JellyfinCodecNameTests
{
    [Theory]
    [InlineData(ElementaryStreamCodec.Mpeg2Video, "mpeg2video")]
    [InlineData(ElementaryStreamCodec.Mpeg4Video, "mpeg4")]
    [InlineData(ElementaryStreamCodec.H264, "h264")]
    [InlineData(ElementaryStreamCodec.Hevc, "hevc")]
    [InlineData(ElementaryStreamCodec.MpegAudioLayer2, "mp2")]
    [InlineData(ElementaryStreamCodec.Aac, "aac")]
    [InlineData(ElementaryStreamCodec.AacLatm, "aac_latm")]
    [InlineData(ElementaryStreamCodec.Ac3, "ac3")]
    [InlineData(ElementaryStreamCodec.Eac3, "eac3")]
    [InlineData(ElementaryStreamCodec.Dts, "dts")]
    [InlineData(ElementaryStreamCodec.DvbSubtitle, "dvb_subtitle")]
    [InlineData(ElementaryStreamCodec.DvbTeletext, "dvb_teletext")]
    public void EveryCodecKeepsTheNameItAlwaysHad(ElementaryStreamCodec codec, string expected)
    {
        Assert.Equal(expected, JellyfinCodecNames.For(codec));
    }

    [Fact]
    public void AStreamNothingIdentifiedIsLeftUnnamed()
    {
        // Jellyfin reads an unnamed codec as a track it cannot match, which for a stream nothing
        // has identified is the truth. Inventing a name would be worse than saying nothing.
        Assert.Null(JellyfinCodecNames.For(ElementaryStreamCodec.Unknown));
    }

    [Fact]
    public void EveryCodecTheCoreCanNameHasAJellyfinName()
    {
        // A value added to the core vocabulary without a name here would silently become a track
        // no device profile matches.
        var unnamed = Enum.GetValues<ElementaryStreamCodec>()
            .Where(codec => codec != ElementaryStreamCodec.Unknown)
            .Where(codec => JellyfinCodecNames.For(codec) is null)
            .ToList();

        Assert.Empty(unnamed);
    }
}
