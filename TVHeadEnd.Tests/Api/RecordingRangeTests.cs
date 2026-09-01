using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Api;
using TVHeadEnd.Configuration;
using TVHeadEnd.Tvheadend;
using Xunit;

namespace TVHeadEnd.Tests.Api;

/// <summary>
/// Whether a viewer can seek inside a recording.
/// </summary>
/// <remarks>
/// <para>
/// This endpoint exists for one reason: TVHeadend answers a range request but never says it does,
/// and FFmpeg gives up on an input it believes it cannot seek in. Everything that makes seeking
/// work is a header this method sets or forwards, and not one of them was covered until now --
/// the endpoint could not be built in a test while it reached for a plugin singleton and a sealed
/// connection.
/// </para>
/// <para>
/// A real listener stands in for TVHeadend rather than a stubbed handler, because what is under
/// test is the shape of an HTTP exchange, and a stub would only replay whatever this test believed
/// that shape to be.
/// </para>
/// </remarks>
public class RecordingRangeTests
{
    private const string RecordingId = "844806511";

    [Fact]
    public async Task WhatTheViewerAsksForIsWhatTvheadendIsAsked()
    {
        await using var upstream = new RecordingServer();
        var (controller, _) = Controller(upstream, range: "bytes=1000-1999");

        await controller.GetRecording(Token(), CancellationToken.None);

        Assert.Equal("bytes=1000-1999", upstream.LastRange);
    }

    [Fact]
    public async Task AskingForTheWholeRecordingAsksForTheWholeRecording()
    {
        // No range is not the same as a range starting at zero: an invented one would turn every
        // plain fetch into a partial response and change what the client is told it received.
        await using var upstream = new RecordingServer();
        var (controller, _) = Controller(upstream, range: null);

        await controller.GetRecording(Token(), CancellationToken.None);

        Assert.Null(upstream.LastRange);
    }

    [Fact]
    public async Task APartialAnswerStaysPartialAndKeepsItsRange()
    {
        await using var upstream = new RecordingServer { Partial = true };
        var (controller, response) = Controller(upstream, range: "bytes=10-19");

        await controller.GetRecording(Token(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status206PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-19/2048", response.Headers.ContentRange.ToString());
        Assert.Equal(10, response.ContentLength);
    }

    [Fact]
    public async Task TheSeekabilityTvheadendNeverAdvertisesIsStatedHere()
    {
        // The whole reason for this endpoint. Without it FFmpeg reads the recording as a stream
        // it cannot seek in and dies before producing a frame.
        await using var upstream = new RecordingServer();
        var (controller, response) = Controller(upstream, range: null);

        await controller.GetRecording(Token(), CancellationToken.None);

        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.Equal(2048, response.ContentLength);
    }

    [Fact]
    public async Task JellyfinIsNotAskedToRangeSomethingThatWasAlreadyRanged()
    {
        // The range was answered upstream. Letting Jellyfin apply a second one to the bytes coming
        // back would slice a slice.
        await using var upstream = new RecordingServer();
        var (controller, _) = Controller(upstream, range: null);

        var result = await controller.GetRecording(Token(), CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.False(file.EnableRangeProcessing);
    }

    [Fact]
    public async Task AHeadAsksTvheadendTheSameQuestionAndCarriesNoBody()
    {
        // One method for both verbs, so they cannot describe the same recording differently --
        // which is exactly what happened when they were two.
        await using var upstream = new RecordingServer();
        var (controller, response) = Controller(upstream, range: null, method: HttpMethods.Head);

        var result = await controller.GetRecording(Token(), CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("HEAD", upstream.LastMethod);
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.Equal(2048, response.ContentLength);
    }

    [Fact]
    public async Task ARecordingTvheadendNoLongerHasIsGone()
    {
        await using var upstream = new RecordingServer { Status = HttpStatusCode.NotFound };
        var (controller, _) = Controller(upstream, range: null);

        var result = await controller.GetRecording(Token(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task AServerThatAnsweredBadlyIsReportedAsTheGatewayItIs()
    {
        // Not a 404: the recording may well be there, and telling a client it is gone would have
        // Jellyfin remove an item over a server hiccup.
        await using var upstream = new RecordingServer { Status = HttpStatusCode.InternalServerError };
        var (controller, _) = Controller(upstream, range: null);

        var result = await controller.GetRecording(Token(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task AnAddressNobodyHereMintedIsNotEvenLookedUp()
    {
        await using var upstream = new RecordingServer();
        var (controller, _) = Controller(upstream, range: null);

        var result = await controller.GetRecording("not-one-of-ours", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, upstream.Requests);
    }

    private static string Token()
        => TvheadendAccessToken.Create(RecordingId, Secret().Ensure());

    private static TvheadendAccessSecret Secret()
        => new(new FixedConfiguration(), NullLogger<TvheadendAccessSecret>.Instance);

    private static (TvHeadendRecordingsController Controller, HttpResponse Response) Controller(
        RecordingServer upstream,
        string? range,
        string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (range is not null)
        {
            context.Request.Headers.Range = range;
        }

        var controller = new TvHeadendRecordingsController(
            new FixedEndpoint(upstream.BaseUrl),
            Secret(),
            new PlainHttpClientFactory(),
            NullLogger<TvHeadendRecordingsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        return (controller, context.Response);
    }

    /// <summary>
    /// A configuration that already holds a secret, so nothing is created or saved.
    /// </summary>
    private sealed class FixedConfiguration : IPluginConfigurationSource
    {
        private readonly PluginConfiguration _configuration = new()
        {
            RecordingAccessSecret = "4e2a1f6b8c0d3e5f7a9b1c3d5e7f9a1b3c5d7e9f1a3b5c7d9e1f3a5b7c9d1e3f",
        };

        public event EventHandler? Changed;

        public PluginConfiguration Current => _configuration;

        public void Save() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FixedEndpoint(string baseUrl) : ITvheadendHttpEndpointSource
    {
        public Task<TvheadendHttpEndpoint> GetHttpEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TvheadendHttpEndpoint(
                new Uri(baseUrl).Host,
                new Uri(baseUrl).Port,
                string.Empty,
                string.Empty,
                string.Empty));
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// A stand-in for TVHeadend's recording endpoint, which answers ranges and never says so.
    /// </summary>
    private sealed class RecordingServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _loop;
        private readonly byte[] _payload = new byte[2048];

        private int _requests;

        internal RecordingServer()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUrl = FormattableString.Invariant($"http://127.0.0.1:{port}");
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(() => ServeAsync(_lifetime.Token));
        }

        internal string BaseUrl { get; }

        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        internal bool Partial { get; init; }

        internal string? LastRange { get; private set; }

        internal string? LastMethod { get; private set; }

        internal int Requests => Volatile.Read(ref _requests);

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Close();

            try
            {
                await _loop;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Teardown.
            }

            _lifetime.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _requests);
                LastRange = context.Request.Headers["Range"];
                LastMethod = context.Request.HttpMethod;

                var response = context.Response;
                response.StatusCode = (int)Status;

                if (Status == HttpStatusCode.OK)
                {
                    if (Partial)
                    {
                        response.StatusCode = (int)HttpStatusCode.PartialContent;
                        response.Headers["Content-Range"] = "bytes 10-19/2048";
                        response.ContentLength64 = 10;
                    }
                    else
                    {
                        response.ContentLength64 = _payload.Length;
                    }

                    // Deliberately no Accept-Ranges: that omission is the whole reason the plugin
                    // serves recordings itself.
                    if (!string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.Ordinal))
                    {
                        await response.OutputStream.WriteAsync(
                            _payload.AsMemory(0, (int)response.ContentLength64),
                            cancellationToken);
                    }
                }

                response.Close();
            }
        }
    }
}
