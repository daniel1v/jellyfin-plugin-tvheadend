using TVHeadEnd.Playback;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// Letting a client that spells MPEG-TS the other way match a source that spells it this way.
/// </summary>
/// <remarks>
/// The comparison between a device profile and a media source is literal, and a source can only
/// name one container. So the profile is the side that has to say both -- and only about the one
/// container that genuinely has two names.
/// </remarks>
public class TransportStreamAliasTests
{
    [Theory]
    [InlineData("ts", "ts,mpegts")]
    [InlineData("mpegts", "mpegts,ts")]
    [InlineData("mp4,ts", "mp4,ts,mpegts")]
    public void TheMissingSpellingIsAdded(string stated, string expected)
    {
        Assert.Equal(expected, TransportStreamAliases.Widen(stated));
    }

    [Theory]
    [InlineData("ts,mpegts")]
    [InlineData("mpegts,ts")]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData("matroska,webm")]
    [InlineData("")]
    public void EverythingElseIsLeftExactlyAsItWas(string stated)
    {
        // Both spellings already named, or neither. Nothing to add, and nothing invented: a
        // profile that says nothing about transport streams keeps saying nothing about them.
        Assert.Equal(stated, TransportStreamAliases.Widen(stated));
    }

    [Fact]
    public void ANegativeListIsNotWidened()
    {
        // A leading minus means "everything except these". Adding a name there would take a
        // capability away rather than grant one, which is the opposite of the point.
        Assert.Equal("-ts", TransportStreamAliases.Widen("-ts"));
    }

    [Fact]
    public void NothingStatedStaysNothingStated()
    {
        Assert.Null(TransportStreamAliases.Widen(null));
    }
}
