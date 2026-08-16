using TVHeadEnd.Api;
using Xunit;

namespace TVHeadEnd.Tests.Api;

public class RecordingAccessTokenTests
{
    private const string Secret = "6F1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F9";

    [Fact]
    public void ATokenNamesTheRecordingItWasBuiltFor()
    {
        var token = RecordingAccessToken.Create("1312160563", Secret);

        Assert.True(RecordingAccessToken.TryRead(token, Secret, out var id));
        Assert.Equal("1312160563", id);
    }

    [Fact]
    public void TheBareRecordingIdentifierIsNotEnough()
    {
        // The point of the tag: a TVHeadend identifier is a small number, and the endpoint
        // answers without a session, so anyone could otherwise walk through the recordings.
        Assert.False(RecordingAccessToken.TryRead("1312160563", Secret, out _));
    }

    [Fact]
    public void ATagFromAnotherServerIsRefused()
    {
        var token = RecordingAccessToken.Create("1312160563", RecordingAccessToken.CreateSecret());

        Assert.False(RecordingAccessToken.TryRead(token, Secret, out _));
    }

    [Fact]
    public void AnAlteredTagIsRefused()
    {
        var token = RecordingAccessToken.Create("1312160563", Secret);
        var tampered = token[..^1] + (token[^1] == 'a' ? 'b' : 'a');

        Assert.False(RecordingAccessToken.TryRead(tampered, Secret, out _));
    }

    [Fact]
    public void ATagCannotBeReusedForAnotherRecording()
    {
        var token = RecordingAccessToken.Create("1312160563", Secret);
        var tag = token[(token.LastIndexOf('-') + 1)..];

        Assert.False(RecordingAccessToken.TryRead("867835561-" + tag, Secret, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("abc-")]
    [InlineData("-abc")]
    public void MalformedTokensAreRefusedRatherThanThrowing(string? token)
    {
        Assert.False(RecordingAccessToken.TryRead(token, Secret, out _));
    }

    [Fact]
    public void EachServerGetsItsOwnSecret()
    {
        Assert.NotEqual(RecordingAccessToken.CreateSecret(), RecordingAccessToken.CreateSecret());
    }
}
