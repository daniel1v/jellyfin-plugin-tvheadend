using System;
using System.Linq;
using System.Reflection;
using TVHeadEnd.Api;
using Xunit;

namespace TVHeadEnd.Tests.Api;

/// <summary>
/// How a picture TVHeadend holds reaches a client, and where the credentials for it may go.
/// </summary>
/// <remarks>
/// <para>
/// It did not reach one at all for as long as TVHeadend required authentication. The plugin handed
/// Jellyfin the TVHeadend URL, Jellyfin fetched it with an HTTP client that knows nothing of
/// TVHeadend, and the server answered 401 -- measured against a real server: anonymous 401,
/// authenticated 200 and 4,971 bytes for the same path.
/// </para>
/// <para>
/// The earlier attempt at fixing it put the credentials in the URL, which cannot work: HttpClient
/// ignores the userinfo component of a URI and sends no Authorization header for it. It failed with
/// the same 401 and wrote the TVHeadend password into Jellyfin's database as an image path.
/// </para>
/// </remarks>
public class ArtworkTests
{
    private const string Secret = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string BaseUrl = "http://tvheadend:9981";

    [Theory]
    [InlineData("imagecache/1")]
    [InlineData("/imagecache/1")]
    [InlineData("http://tvheadend:9981/imagecache/1")]
    [InlineData("HTTP://TVHEADEND:9981/imagecache/1")]
    public void EveryWayTvheadendNamesItsOwnImageIsRecognised(string reference)
    {
        // The reference is version dependent: an absolute URL below the per-field threshold, a
        // root-relative path between HTSP v8 and v14, a relative one from v15 on. All three are
        // the same picture on the same server, and all three need the credentials.
        Assert.Equal("imagecache/1", TvheadendArtwork.PathOnTvheadend(reference, BaseUrl));
    }

    [Theory]
    [InlineData("https://picons.example/ard.png")]
    [InlineData("http://some-other-host:9981/imagecache/1")]
    public void AnImageOnAnotherHostIsNotOurs(string reference)
    {
        // An EPG provider's own artwork. It needs no credentials, and the rule that matters is
        // that it must not be sent any: null here is what makes the caller publish it unchanged
        // rather than route it through this server.
        Assert.Null(TvheadendArtwork.PathOnTvheadend(reference, BaseUrl));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("imagecache/1?x=y")]
    [InlineData("imagecache/1#f")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    public void APathThatIsNotAPlainOneUnderTheWebRootIsRefused(string? reference)
    {
        // The token cannot be forged, so this is not the only thing standing between a caller and
        // an arbitrary fetch -- but it is the line that decides where the credentials go, and it
        // does not depend on every caller elsewhere having got it right.
        Assert.Null(TvheadendArtwork.PathOnTvheadend(reference, BaseUrl));
    }

    [Fact]
    public void APathSurvivesBeingCarriedInsideATokenSegment()
    {
        // A path has slashes and a URL path segment cannot, so it is encoded -- and in something
        // that never produces the hyphen the token separates its tag with.
        const string Path = "imagecache/1";

        var encoded = TvheadendArtwork.Encode(Path);

        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('-', encoded);

        Assert.True(TvheadendArtwork.TryDecode(encoded, out var decoded));
        Assert.Equal(Path, decoded);
    }

    [Fact]
    public void SomethingThatIsNotOneOfOurTokensDecodesToNothing()
    {
        Assert.False(TvheadendArtwork.TryDecode("not-hex", out _));
        Assert.False(TvheadendArtwork.TryDecode(string.Empty, out _));
        Assert.False(TvheadendArtwork.TryDecode(null, out _));
    }

    [Fact]
    public void TheAddressPointsAtJellyfinRatherThanAtTvheadend()
    {
        // The whole of the fix. An address on this server needs no TVHeadend credentials, so the
        // one fetch that does need them is made by the code that has them.
        var path = TvHeadendImagesController.ImagePathFor("abcdef-0123456789abcdef");

        Assert.Equal("/TVHeadend/Artwork/abcdef-0123456789abcdef", path);
    }

    [Fact]
    public void TheAddressIsOneTheControllerActuallyServes()
    {
        var route = Attribute<Microsoft.AspNetCore.Mvc.HttpGetAttribute>().Template;

        var prefix = typeof(TvHeadendImagesController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single()
            .Template;

        Assert.Equal("/" + prefix + "/" + route, TvHeadendImagesController.ImagePathFor("{token}"));
    }

    [Fact]
    public void TheAddressCannotBeMintedByWhoeverAsksForIt()
    {
        // The route is anonymous, because Jellyfin's image pipeline fetches it without a session.
        // What stands in for a session is the tag: a path on its own opens nothing, so nobody can
        // point this server at a resource of their choosing.
        var encoded = TvheadendArtwork.Encode("imagecache/1");
        var genuine = TvheadendAccessToken.Create(encoded, Secret);

        Assert.True(TvheadendAccessToken.TryRead(genuine, Secret, out var read));
        Assert.Equal(encoded, read);

        Assert.False(TvheadendAccessToken.TryRead(encoded, Secret, out _));
        Assert.False(TvheadendAccessToken.TryRead(encoded + "-0000000000000000", Secret, out _));
    }

    [Fact]
    public void TheRouteIsReachableWithoutASession()
    {
        // Jellyfin fetches an image from its own pipeline, which carries no session, exactly as it
        // fetches any other remote image. A route that demanded one would 401 for the same reason
        // TVHeadend did.
        Assert.NotNull(Attribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
    }

    [Fact]
    public void TheAddressItselfCarriesNoCredentials()
    {
        // The failure this replaces, kept as a rule: the password ended up in Jellyfin's database
        // as an image path and in the log on every failed fetch, and it never authenticated
        // anything because HttpClient does not send userinfo.
        var endpoint = Endpoint();

        Assert.DoesNotContain('@', endpoint.BaseUrl);
        Assert.DoesNotContain("secret-password", endpoint.CreateApiUrl("imagecache/1"), StringComparison.Ordinal);

        // They travel as a header instead, which is the only form that authenticates anything.
        Assert.True(endpoint.RequiresAuthentication);
        Assert.Contains("Authorization", endpoint.CreateHeaders().Keys);
    }

    [Fact]
    public void CredentialsCanOnlyEverReachTheConfiguredEndpoint()
    {
        // Not a check the controller performs but a property of how it builds the address: it
        // takes a path, and CreateApiUrl puts the configured base URL in front of it. There is no
        // input that makes it produce a different host.
        var endpoint = Endpoint();
        var path = TvheadendArtwork.PathOnTvheadend("imagecache/1", endpoint.BaseUrl);

        Assert.NotNull(path);
        Assert.StartsWith(endpoint.BaseUrl, endpoint.CreateApiUrl(path!), StringComparison.Ordinal);
    }

    private static T Attribute<T>()
        where T : Attribute
    {
        var method = typeof(TvHeadendImagesController)
            .GetMethod(nameof(TvHeadendImagesController.GetArtwork));

        Assert.NotNull(method);

        return method!.GetCustomAttributes(typeof(T), false).Cast<T>().Single();
    }

    private static TVHeadEnd.Tvheadend.TvheadendHttpEndpoint Endpoint()
        => new("tvheadend", 9981, string.Empty, "Frigo", "secret-password");
}
