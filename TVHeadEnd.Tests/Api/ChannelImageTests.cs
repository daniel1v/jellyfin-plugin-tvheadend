using System;
using System.Linq;
using TVHeadEnd.Api;
using Xunit;

namespace TVHeadEnd.Tests.Api;

/// <summary>
/// How a channel's logo reaches a client.
/// </summary>
/// <remarks>
/// <para>
/// It did not, for as long as TVHeadend required authentication. The plugin handed Jellyfin the
/// TVHeadend URL, Jellyfin fetched it with an HTTP client that knows nothing of TVHeadend, and the
/// server answered 401 -- measured against a real server: anonymous 401, authenticated 200 and
/// 4,971 bytes for the same path.
/// </para>
/// <para>
/// The earlier attempt at fixing it put the credentials in the URL, which cannot work: HttpClient
/// ignores the userinfo component of a URI and sends no Authorization header for it. It failed
/// with the same 401 and wrote the TVHeadend password into Jellyfin's database as an image path.
/// </para>
/// </remarks>
public class ChannelImageTests
{
    private const string Secret = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void TheAddressPointsAtJellyfinRatherThanAtTvheadend()
    {
        // The whole of the fix. An address on this server needs no TVHeadend credentials, so the
        // one fetch that does need them is made by the code that has them.
        var path = TvHeadendImagesController.ImagePathFor("42-abcdef0123456789");

        Assert.Equal("/TVHeadend/Channels/42-abcdef0123456789/image", path);
    }

    [Fact]
    public void TheAddressIsOneTheControllerActuallyServes()
    {
        var method = typeof(TvHeadendImagesController)
            .GetMethod(nameof(TvHeadendImagesController.GetChannelImage));

        Assert.NotNull(method);

        var route = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.HttpGetAttribute>()
            .Single()
            .Template;

        var prefix = typeof(TvHeadendImagesController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single()
            .Template;

        Assert.Equal("/" + prefix + "/" + route, TvHeadendImagesController.ImagePathFor("{token}"));
    }

    [Fact]
    public void TheAddressCannotBeGuessedFromTheChannelNumber()
    {
        // The route is anonymous, because Jellyfin's image pipeline fetches it without a session.
        // What stands in for a session is the tag: a channel number on its own opens nothing.
        var genuine = TvheadendAccessToken.Create("42", Secret);

        Assert.True(TvheadendAccessToken.TryRead(genuine, Secret, out var channelId));
        Assert.Equal("42", channelId);

        Assert.False(TvheadendAccessToken.TryRead("42", Secret, out _));
        Assert.False(TvheadendAccessToken.TryRead("42-0000000000000000", Secret, out _));
    }

    [Fact]
    public void TheTokenNamesAChannelAndNeverAnAddress()
    {
        // Which is what keeps the route from being a way to make this server fetch arbitrary URLs.
        // The icon is looked up from the catalog by the identifier the token carries; nothing a
        // caller sends reaches the request the plugin makes.
        var token = TvheadendAccessToken.Create("42", Secret);

        Assert.DoesNotContain("http", token, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/", token, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRouteIsReachableWithoutASession()
    {
        // Jellyfin fetches a channel image from its own image pipeline, which carries no session,
        // exactly as it fetches any other remote image. A route that demanded one would 401 for
        // the same reason TVHeadend did.
        var method = typeof(TvHeadendImagesController)
            .GetMethod(nameof(TvHeadendImagesController.GetChannelImage));

        Assert.NotNull(method);
        Assert.Single(method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false));
    }

    [Theory]
    [InlineData("imagecache/1", "http://tvheadend:9981/imagecache/1")]
    [InlineData("/imagecache/1", "http://tvheadend:9981/imagecache/1")]
    [InlineData("https://picons.example/ard.png", "https://picons.example/ard.png")]
    public void AnIconReferenceBecomesAnAddress(string icon, string expected)
    {
        // TVHeadend's reference is version dependent -- absolute, root-relative or relative -- and
        // an EPG provider may supply an absolute URL of its own.
        var endpoint = Endpoint();

        Assert.Equal(expected, endpoint.ResolveImageUrl(icon));
    }

    [Fact]
    public void CredentialsAreOnlyEverSentToTvheadend()
    {
        // A channel icon can be an absolute URL pointing anywhere at all. This is the test for the
        // rule the controller applies before attaching the header: the address has to be on the
        // TVHeadend endpoint, or it goes out bare.
        var endpoint = Endpoint();

        var ours = endpoint.ResolveImageUrl("imagecache/1");
        var theirs = endpoint.ResolveImageUrl("https://picons.example/ard.png");

        Assert.StartsWith(endpoint.BaseUrl, ours, StringComparison.OrdinalIgnoreCase);
        Assert.False(theirs!.StartsWith(endpoint.BaseUrl, StringComparison.OrdinalIgnoreCase));

        // And the header exists to be attached in the first place, which is the whole point.
        Assert.True(endpoint.RequiresAuthentication);
        Assert.Contains("Authorization", endpoint.CreateHeaders().Keys);
    }

    [Fact]
    public void TheAddressItselfCarriesNoCredentials()
    {
        // The failure this replaces, kept as a rule: the password ended up in Jellyfin's database
        // as an image path and in the log on every failed fetch, and it never authenticated
        // anything because HttpClient does not send userinfo.
        var endpoint = Endpoint();

        Assert.DoesNotContain("@", endpoint.ResolveImageUrl("imagecache/1")!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", endpoint.BaseUrl, StringComparison.Ordinal);
    }

    private static TVHeadEnd.Tvheadend.TvheadendHttpEndpoint Endpoint()
        => new("tvheadend", 9981, string.Empty, "Frigo", "secret-password");
}
