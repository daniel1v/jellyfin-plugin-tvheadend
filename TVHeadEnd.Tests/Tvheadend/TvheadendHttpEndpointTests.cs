using TVHeadEnd.Tvheadend;
using Xunit;

namespace TVHeadEnd.Tests.Tvheadend;

/// <summary>
/// The address a live channel is fetched from, which is where the whole live path begins.
/// </summary>
/// <remarks>
/// <para>
/// Everything this plugin does with a live stream rests on one premise: TVHeadend is the tuner and
/// not the encoder, so what arrives is the broadcast itself, with its own PCR, program tables and
/// random access points intact. That premise is a single query parameter. Ask for any other
/// profile and TVHeadend re-muxes or transcodes, the program map stops describing what actually
/// arrives, and every conclusion the conditioner draws from it -- the stream order, the join
/// points, the IDR question -- becomes a statement about a stream nobody is watching.
/// </para>
/// <para>
/// Nothing failing would say so. The stream would still play, the tests that read program maps
/// would still pass on their synthetic input, and only the measurements would quietly stop
/// holding. That is why the profile is pinned here rather than left as an implementation detail
/// of whichever class happens to build the URL.
/// </para>
/// </remarks>
public class TvheadendHttpEndpointTests
{
    [Fact]
    public void TheOnlyProfileThisPluginAsksForIsTheOneThatChangesNothing()
    {
        // Pinned as a value, because the call site names the constant and a constant can be
        // edited without any caller changing shape.
        Assert.Equal("pass", TvheadendHttpEndpoint.PassProfile);
    }

    [Fact]
    public void AChannelIsStreamedFromTvheadendsOwnPassThroughRoute()
    {
        var url = Endpoint().CreateChannelStreamUrl("1460599120", TvheadendHttpEndpoint.PassProfile);

        Assert.Equal("http://tvh.local:9981/stream/channelid/1460599120?profile=pass", url);
    }

    [Fact]
    public void TheWebRootTheServerReportedIsPartOfTheAddress()
    {
        // Only known from the handshake, and a server behind a path prefix answers nothing
        // without it.
        var endpoint = new TvheadendHttpEndpoint("tvh.local", 9981, "/tvheadend", string.Empty, string.Empty);

        Assert.Equal(
            "http://tvh.local:9981/tvheadend/stream/channelid/42?profile=pass",
            endpoint.CreateChannelStreamUrl("42", TvheadendHttpEndpoint.PassProfile));
    }

    [Fact]
    public void AChannelIdentifierIsEncodedRatherThanPastedIn()
    {
        var url = Endpoint().CreateChannelStreamUrl("a b&c", TvheadendHttpEndpoint.PassProfile);

        Assert.Equal("http://tvh.local:9981/stream/channelid/a+b%26c?profile=pass", url);
    }

    [Fact]
    public void AskingForNoProfileLeavesTheServerToItsDefault()
    {
        // The distinction is real even though nothing takes this branch today: no profile means
        // the server decides, which is not the same as asking for the pass-through one.
        var url = Endpoint().CreateChannelStreamUrl("42", null);

        Assert.Equal("http://tvh.local:9981/stream/channelid/42", url);
        Assert.DoesNotContain("profile", url, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheApiAndTheStreamShareOneServerAddress()
    {
        // The recordings path fetches "dvrfile/<id>" from the same endpoint the stream comes from.
        // Two ways of building a base address are two ways of pointing at different servers.
        var endpoint = new TvheadendHttpEndpoint("tvh.local", 9981, "/tvheadend", string.Empty, string.Empty);

        Assert.StartsWith(
            "http://tvh.local:9981/tvheadend/",
            endpoint.CreateApiUrl("dvrfile/844806511"),
            System.StringComparison.Ordinal);
    }

    private static TvheadendHttpEndpoint Endpoint()
        => new("tvh.local", 9981, string.Empty, string.Empty, string.Empty);
}
