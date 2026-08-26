using System;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.LiveTv;
using Xunit;

namespace TVHeadEnd.Tests.LiveTv;

/// <summary>
/// What happens when TVHeadend answers a DVR request by declining it.
/// </summary>
/// <remarks>
/// TVHeadend does not report a rejected timer as an error; it replies normally, with a success
/// flag set to false. So a reply arriving is not the same as the work being done, and treating it
/// as such told Jellyfin the timer had been set: it reported success to the client, scheduled
/// nothing, and the recording quietly never happened.
/// </remarks>
public class DvrRefusalTests
{
    [Fact]
    public void ARefusalIsAFailureAndCarriesTheReasonTvheadendGave()
    {
        var reply = new HtspMessage();
        reply.Set("success", 0);
        reply.Set("error", "Access denied");

        var refused = Assert.Throws<HtspException>(() => TvheadendDvr.EnsureAccepted(reply));

        // The reason has to survive: "it did not work" sends somebody to the wrong place.
        Assert.Contains("Access denied", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalWithoutAReasonStillFails()
    {
        var reply = new HtspMessage();
        reply.Set("success", 0);

        Assert.Throws<HtspException>(() => TvheadendDvr.EnsureAccepted(reply));
    }

    [Fact]
    public void AnAcceptedRequestPassesThrough()
    {
        var reply = new HtspMessage();
        reply.Set("success", 1);

        TvheadendDvr.EnsureAccepted(reply);
    }

    [Fact]
    public void AReplyThatDoesNotMentionSuccessIsNotARefusal()
    {
        // The flag is only meaningful where it is present. Reading its absence as a refusal would
        // fail every operation on a server or a request that does not use it.
        TvheadendDvr.EnsureAccepted(new HtspMessage());
    }
}
