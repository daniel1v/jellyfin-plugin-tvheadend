using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Model;
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
    public async Task ASubscriptionMessageReachesTheSubscriptionItNames()
    {
        // Two subscriptions on one connection. Routing by subscriptionId is what keeps one
        // channel's description from being applied to another's stream.
        await using var server = new FakeHtspServer();
        server.OnRequest = (request, _) => IsHandshake(request.Method)
            ? Handshake(request)
            : new HtspMessage();

        server.Start();
        await using var connection = await ConnectAsync(server);

        var first = await connection.SubscribeAsync(101, new HtspSubscriptionOptions(), CancellationToken.None);
        var second = await connection.SubscribeAsync(202, new HtspSubscriptionOptions(), CancellationToken.None);

        await server.SendAsync(SubscriptionStart(second.SubscriptionId, videoPid: 0, width: 1920, height: 1080));

        var describedSecond = await second.WaitForStartAsync(CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(1920, describedSecond.Video!.Width);

        // The first was never described, and must not have picked up the other's message.
        Assert.Null(first.Start);
        Assert.Equal(HtspSubscriptionState.Starting, first.State);
    }

    [Fact]
    public async Task SubscribingFiltersEveryStreamIndexStraightAway()
    {
        // The media arrives over HTTP; this subscription exists to be told what it is. Leaving
        // the payload enabled would move the whole broadcast a second time.
        await using var server = new FakeHtspServer();
        server.OnRequest = (request, _) => IsHandshake(request.Method)
            ? Handshake(request)
            : new HtspMessage();

        server.Start();
        await using var connection = await ConnectAsync(server);

        await connection.SubscribeAsync(
            7,
            new HtspSubscriptionOptions { DisableAllStreams = true },
            CancellationToken.None);

        var filter = await server.WaitForRequestAsync("subscriptionFilterStream");
        var disabled = filter.GetInt64List("disable");

        Assert.Equal(HtspSubscription.FilteredStreamCount, disabled.Count);
        Assert.Equal(0, disabled[0]);
        Assert.Equal(HtspSubscription.FilteredStreamCount - 1, disabled[^1]);

        // And it has to come after the subscription exists, or the server has nothing to apply
        // it to.
        var methods = server.ReceivedRequests().Select(request => request.Method).ToList();
        Assert.True(methods.IndexOf("subscribe") < methods.IndexOf("subscriptionFilterStream"));
    }

    [Fact]
    public async Task ASubscriptionAcceptsBeingDescribedAgain()
    {
        // TVHeadend re-describes a stream whenever the broadcast changes shape. The subscription
        // outlives the first description precisely so that this is visible.
        await using var server = new FakeHtspServer();
        server.OnRequest = (request, _) => IsHandshake(request.Method)
            ? Handshake(request)
            : new HtspMessage();

        server.Start();
        await using var connection = await ConnectAsync(server);

        var subscription = await connection.SubscribeAsync(9, new HtspSubscriptionOptions(), CancellationToken.None);

        var descriptions = new System.Collections.Concurrent.ConcurrentQueue<HtspSubscriptionStart>();
        subscription.Started += (_, start) => descriptions.Enqueue(start);

        await server.SendAsync(SubscriptionStart(subscription.SubscriptionId, videoPid: 0, width: 720, height: 576));
        await subscription.WaitForStartAsync(CancellationToken.None).WaitAsync(Patience);

        await server.SendAsync(SubscriptionStart(subscription.SubscriptionId, videoPid: 0, width: 1920, height: 1080));

        await WaitUntil(() => descriptions.Count == 2);

        Assert.Equal(2, descriptions.Count);
        Assert.Equal(1920, subscription.Start!.Video!.Width);
        Assert.Equal(HtspSubscriptionState.Running, subscription.State);
    }

    [Fact]
    public async Task AStopFromTheServerEndsTheSubscription()
    {
        await using var server = new FakeHtspServer();
        server.OnRequest = (request, _) => IsHandshake(request.Method)
            ? Handshake(request)
            : new HtspMessage();

        server.Start();
        await using var connection = await ConnectAsync(server);

        var subscription = await connection.SubscribeAsync(11, new HtspSubscriptionOptions(), CancellationToken.None);

        await server.SendAsync(new HtspMessage()
            .Set("method", "subscriptionStop")
            .Set("subscriptionId", subscription.SubscriptionId)
            .Set("status", "noFreeAdapter"));

        await WaitUntil(() => subscription.State == HtspSubscriptionState.Stopped);

        // A caller still waiting to be told what the stream is learns that it never will be,
        // rather than waiting for ever.
        await Assert.ThrowsAsync<HtspException>(
            () => subscription.WaitForStartAsync(CancellationToken.None).WaitAsync(Patience));
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

    private static HtspMessage SubscriptionStart(int subscriptionId, int videoPid, int width, int height)
        => new HtspMessage()
            .Set("method", "subscriptionStart")
            .Set("subscriptionId", subscriptionId)
            .Set(
                "streams",
                [
                    new HtspMessage()
                        .Set("index", 1)
                        .Set("type", "H264")
                        .Set("width", width)
                        .Set("height", height)
                        .Set("duration", 3600),
                    new HtspMessage()
                        .Set("index", 2)
                        .Set("type", "MPEG2AUDIO")
                        .Set("language", "deu")
                        .Set("channels", 2)
                        .Set("rate", 3),
                ])
            .Set(
                "sourceinfo",
                new HtspMessage()
                    .Set("mux_uuid", "mux-1")
                    .Set("service", "Das Erste HD"));

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
