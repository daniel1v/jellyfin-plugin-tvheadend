using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// The single rule, in all of its combinations.
/// </summary>
/// <remarks>
/// Live TV and recordings reach Jellyfin through different hooks and ask this the same way. Both
/// costs of getting it wrong are real: a needless re-encode is a processor core per viewer, and a
/// missed one is a recording that never starts and reports nothing.
/// </remarks>
public class PlaybackCompatibilityPolicyTests
{
    [Theory]
    [InlineData(true, H264EntryPointEvidence.RecoveryOnlyObserved, true)]
    [InlineData(true, H264EntryPointEvidence.IdrObserved, false)]
    [InlineData(true, H264EntryPointEvidence.Insufficient, false)]
    [InlineData(false, H264EntryPointEvidence.RecoveryOnlyObserved, false)]
    [InlineData(false, H264EntryPointEvidence.IdrObserved, false)]
    [InlineData(false, H264EntryPointEvidence.Insufficient, false)]
    public void BothHalvesHaveToBeEstablished(bool clientNeedsIdr, H264EntryPointEvidence evidence, bool expected)
    {
        Assert.Equal(expected, PlaybackCompatibilityPolicy.RequiresVideoReencode(clientNeedsIdr, evidence));
    }

    [Fact]
    public void NotHavingLookedIsNotEvidenceOfAbsence()
    {
        // The distinction the whole design rests on. Material nobody has read enough of is
        // delivered as it is -- which also covers every broadcast that is not H.264, because
        // nothing ever reads one of those for access points.
        Assert.False(PlaybackCompatibilityPolicy.RequiresVideoReencode(true, H264EntryPointEvidence.Insufficient));
    }
}
