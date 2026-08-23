using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tvheadend.Htsp;

using Tvheadend.Htsp.Protocol;
using Xunit;

namespace TVHeadEnd.Tests.Htsp;

/// <summary>
/// How the connection multiplexes: which reply belongs to which request, and which subscription
/// an asynchronous message belongs to.
/// </summary>
public class HtspConnectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AReplyReachesItsOwnRequestEvenWhenRepliesComeBackOutOfOrder()
    {
        // One socket carries every request, and TVHeadend answers when each one is ready rather
        // than in the order they arrived. Correlating by sequence number is the whole reason the
        // connection can be shared.
        await using var server = new FakeHtspServer();
        var pending = new System.Collections.Concurrent.ConcurrentBag<HtspMessage>();

        server.OnRequest = (request, _) =>
        {
            if (IsHandshake(request.Method))
            {
                return Handshake(request);
            }

            // Held back, then answered in reverse.
            pending.Add(request);
            return null;
        };

        server.Start();
        await using var connection = await ConnectAsync(server);

        var slow = connection.SendRequestAsync(HtspMessage.Create("slow"), CancellationToken.None);
        var quick = connection.SendRequestAsync(HtspMessage.Create("quick"), CancellationToken.None);

        await server.WaitForRequestAsync("slow");
        await server.WaitForRequestAsync("quick");

        foreach (var request in pending.OrderByDescending(candidate => candidate.Method))
        {
            await server.SendAsync(new HtspMessage()
                .Set("seq", request.GetInt64("seq")!.Value)
                .Set("answering", request.Method));
        }

        Assert.Equal("slow", (await slow.WaitAsync(Patience)).GetString("answering"));
        Assert.Equal("quick", (await quick.WaitAsync(Patience)).GetString("answering"));
    }

    [Fact]
    public async Task AnErrorReplyBecomesAnException()
    {
        await using var server = new FakeHtspServer();
        server.OnRequest = (request, _) => IsHandshake(request.Method)
            ? Handshake(request)
            : new HtspMessage().Set("error", "Channel does not exist");

        server.Start();
        await using var connection = await ConnectAsync(server);

        var failure = await Assert.ThrowsAsync<HtspRequestException>(
            () => connection.SendRequestAsync(HtspMessage.Create("subscribe"), CancellationToken.None));

        Assert.Equal("Channel does not exist", failure.Error);
    }

    [Fact]
    public async Task AMessageBelongingToNothingIsRaisedAsAnEvent()
    {
        // The channel and DVR feed arrives this way, with no sequence number and no
        // subscription.
        await using var server = new FakeHtspServer();
        server.OnRequest = (request, _) => IsHandshake(request.Method)
            ? Handshake(request)
            : new HtspMessage();

        server.Start();
        await using var connection = await ConnectAsync(server);

        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        connection.MessageReceived += (_, message) => seen.Enqueue(message.Method);

        await server.SendAsync(new HtspMessage().Set("method", "channelAdd").Set("channelId", 1));
        await server.SendAsync(new HtspMessage().Set("method", "initialSyncCompleted"));

        await WaitUntil(() => seen.Count == 2);

        Assert.Equal(["channelAdd", "initialSyncCompleted"], seen);
    }

    [Fact]
    public async Task LosingTheConnectionFailsWhateverWasWaitingOnIt()
    {
        // Nothing may be left parked on a task that can never complete.
        await using var server = new FakeHtspServer();
        server.OnRequest = (request, _) => IsHandshake(request.Method)
            ? Handshake(request)
            : null;

        server.Start();
        var connection = await ConnectAsync(server);

        var pending = connection.SendRequestAsync(HtspMessage.Create("getEvents"), CancellationToken.None);
        await server.WaitForRequestAsync("getEvents");

        await server.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Patience));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task AConnectionThatHasEndedNeverReportsItselfUsableAgain()
    {
        // The socket surviving proves nothing. A connection that has been failed by the far end
        // closing, by the network going away or by the framing losing step can carry nothing, and
        // anything that believed the socket would keep handing work to it for ever instead of
        // opening a new one.
        await using var server = new FakeHtspServer();
        server.OnRequest = (request, _) => IsHandshake(request.Method) ? Handshake(request) : null;

        server.Start();
        var connection = await ConnectAsync(server);
        Assert.True(connection.IsConnected);

        await server.DisposeAsync();
        await connection.Closed.WaitAsync(Patience);

        Assert.False(connection.IsConnected);
        await connection.DisposeAsync();
    }

    private static bool IsHandshake(string method)
        => string.Equals(method, "hello", StringComparison.Ordinal)
            || string.Equals(method, "authenticate", StringComparison.Ordinal);

    private static HtspMessage Handshake(HtspMessage request)
        => string.Equals(request.Method, "hello", StringComparison.Ordinal) ? Hello() : new HtspMessage();

    private static HtspMessage Hello() => new HtspMessage()
        .Set("htspversion", 44)
        .Set("servername", "FakeTvheadend")
        .Set("serverversion", "4.3")
        .Set("challenge", new byte[32]);

    private static async Task<HtspConnection> ConnectAsync(FakeHtspServer server)
    {
        var connection = new HtspConnection(new HtspConnectionOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            UserName = "tester",
            Password = "secret",
            ConnectTimeout = Patience,
            RequestTimeout = Patience,
        });

        await connection.ConnectAsync(CancellationToken.None);
        return connection;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The expected state was never reached.");
    }
}
