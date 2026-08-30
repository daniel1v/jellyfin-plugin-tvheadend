using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Playback;
using TVHeadEnd.Recordings;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// The one place a recording's playback is made client-dependent, and everything it must leave
/// alone to get there.
/// </summary>
/// <remarks>
/// The media source the channel publishes stays the same for every viewer; this is what makes one
/// request different, and only when both halves of the rule are actually established. Nearly all
/// of these tests are about it doing nothing.
/// </remarks>
public class RecordingPlaybackCompatibilityFilterTests
{
    private const string RecordingId = "dvr-4711";

    [Fact]
    public async Task AnAffectedClientPlayingARecoveryOnlyRecordingLosesEveryWayOfCopyingIt()
    {
        // The case this exists for. Withdrawing direct play alone would still leave Jellyfin
        // transcoding with the video copied, which hands the same pictures to the same decoder.
        var context = Request("Jellyfin for Android", Recording());

        await Run(context, H264EntryPointEvidence.RecoveryOnlyObserved);

        Assert.False((bool)context.ActionArguments["enableDirectPlay"]!);
        Assert.False((bool)context.ActionArguments["enableDirectStream"]!);
        Assert.False((bool)context.ActionArguments["allowVideoStreamCopy"]!);
    }

    [Fact]
    public async Task WhatJellyfinMayDoInsteadIsNeverDictated()
    {
        // Transcoding is offered or withheld by Jellyfin and the client's own profile. Forcing it
        // on would be this plugin deciding something it has no standing to decide, and forcing it
        // off would leave a client with nothing at all.
        var context = Request("Jellyfin for Android", Recording());
        context.ActionArguments["enableTranscoding"] = false;

        await Run(context, H264EntryPointEvidence.RecoveryOnlyObserved);

        Assert.False((bool)context.ActionArguments["enableTranscoding"]!);
    }

    [Fact]
    public async Task AClientThatStartsOnAnythingIsLeftAlone()
    {
        var context = Request("Jellyfin Web", Recording());

        await Run(context, H264EntryPointEvidence.RecoveryOnlyObserved);

        AssertUntouched(context);
    }

    [Fact]
    public async Task AnUnauthenticatedRequestIsLeftAlone()
    {
        var context = Request(client: null, Recording());

        await Run(context, H264EntryPointEvidence.RecoveryOnlyObserved);

        AssertUntouched(context);
    }

    [Fact]
    public async Task ARecordingWithAnIdrIsLeftAlone()
    {
        var context = Request("Jellyfin for Android", Recording());

        await Run(context, H264EntryPointEvidence.IdrObserved);

        AssertUntouched(context);
    }

    [Fact]
    public async Task ARecordingTooShortToSayIsLeftAlone()
    {
        // Not having looked is not evidence of absence, and an MPEG-2 recording is never looked
        // at in the first place -- both arrive here as the same answer.
        var context = Request("Jellyfin for Android", Recording());

        await Run(context, H264EntryPointEvidence.Insufficient);

        AssertUntouched(context);
    }

    [Fact]
    public async Task ARecordingThatCouldNotBeAnalysedIsLeftAlone()
    {
        var context = Request("Jellyfin for Android", Recording());
        var analyser = new StubAnalyser(H264EntryPointEvidence.RecoveryOnlyObserved) { Throws = true };

        await Filter(context, analyser).OnActionExecutionAsync(context, Next(context));

        AssertUntouched(context);
    }

    [Fact]
    public async Task AnItemOfSomebodyElsesIsLeftAlone()
    {
        var foreign = new Video { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid(), ExternalId = RecordingId };
        var context = Request("Jellyfin for Android", foreign);

        var analyser = new StubAnalyser(H264EntryPointEvidence.RecoveryOnlyObserved);
        await Filter(context, analyser).OnActionExecutionAsync(context, Next(context));

        AssertUntouched(context);
        Assert.Equal(0, analyser.Calls);
    }

    [Fact]
    public async Task OneOfThisPluginsLiveChannelsIsLeftAlone()
    {
        // Live TV makes the same decision, elsewhere and differently: it has a stream being
        // opened and can build the media source for the viewer who opened it.
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = RecordingId };
        var context = Request("Jellyfin for Android", channel);

        var analyser = new StubAnalyser(H264EntryPointEvidence.RecoveryOnlyObserved);
        await Filter(context, analyser).OnActionExecutionAsync(context, Next(context));

        AssertUntouched(context);
        Assert.Equal(0, analyser.Calls);
    }

    [Fact]
    public async Task AnItemTheLibraryDoesNotKnowIsLeftAlone()
    {
        var context = Request("Jellyfin for Android", item: null);

        var analyser = new StubAnalyser(H264EntryPointEvidence.RecoveryOnlyObserved);
        await Filter(context, analyser).OnActionExecutionAsync(context, Next(context));

        AssertUntouched(context);
        Assert.Equal(0, analyser.Calls);
    }

    [Fact]
    public async Task ARecordingWithNoTvheadendIdentifierIsLeftAlone()
    {
        var context = Request("Jellyfin for Android", Recording(externalId: string.Empty));

        var analyser = new StubAnalyser(H264EntryPointEvidence.RecoveryOnlyObserved);
        await Filter(context, analyser).OnActionExecutionAsync(context, Next(context));

        AssertUntouched(context);
        Assert.Equal(0, analyser.Calls);
    }

    [Fact]
    public async Task EveryOtherEndpointIsLeftAlone()
    {
        // A global filter runs on every action in the server. This one applies to exactly one.
        var context = Request("Jellyfin for Android", Recording(), route: "Items/{itemId}/Images/{imageType}");

        var analyser = new StubAnalyser(H264EntryPointEvidence.RecoveryOnlyObserved);
        await Filter(context, analyser).OnActionExecutionAsync(context, Next(context));

        AssertUntouched(context);
        Assert.Equal(0, analyser.Calls);
    }

    [Fact]
    public async Task TheGetFormOfPlaybackInfoIsLeftAlone()
    {
        // It takes none of these parameters, so setting them would mean inventing arguments the
        // action has no place to put.
        var context = Request("Jellyfin for Android", Recording(), method: HttpMethods.Get);

        var analyser = new StubAnalyser(H264EntryPointEvidence.RecoveryOnlyObserved);
        await Filter(context, analyser).OnActionExecutionAsync(context, Next(context));

        AssertUntouched(context);
        Assert.Equal(0, analyser.Calls);
    }

    [Fact]
    public async Task TheActionRunsWhateverTheDecisionWas()
    {
        var context = Request("Jellyfin for Android", Recording());
        var ran = false;

        await Filter(context, new StubAnalyser(H264EntryPointEvidence.RecoveryOnlyObserved))
            .OnActionExecutionAsync(
                context,
                () =>
                {
                    ran = true;
                    return Task.FromResult(Executed(context));
                });

        Assert.True(ran);
    }

    private static void AssertUntouched(ActionExecutingContext context)
    {
        Assert.DoesNotContain("enableDirectPlay", context.ActionArguments);
        Assert.DoesNotContain("enableDirectStream", context.ActionArguments);
        Assert.DoesNotContain("allowVideoStreamCopy", context.ActionArguments);
    }

    private static Task Run(ActionExecutingContext context, H264EntryPointEvidence evidence)
        => Filter(context, new StubAnalyser(evidence)).OnActionExecutionAsync(context, Next(context));

    private static RecordingPlaybackCompatibilityFilter Filter(ActionExecutingContext context, IRecordingAnalyser analyser)
        => new(
            LibraryOf((BaseItem?)context.HttpContext.Items["item"]),
            analyser,
            NullLogger<RecordingPlaybackCompatibilityFilter>.Instance);

    private static ActionExecutionDelegate Next(ActionExecutingContext context)
        => () => Task.FromResult(Executed(context));

    private static ActionExecutedContext Executed(ActionExecutingContext context)
        => new(context, context.Filters, context.Controller);

    private static Video Recording(string externalId = RecordingId) => new()
    {
        Id = Guid.NewGuid(),
        ChannelId = LibraryProxy.RecordingsChannelIdentifier,
        ExternalId = externalId,
    };

    /// <summary>
    /// A request as MVC hands it to a filter: bound arguments, an authenticated session, and the
    /// route template of the action about to run.
    /// </summary>
    private static ActionExecutingContext Request(
        string? client,
        BaseItem? item,
        string route = "Items/{itemId}/PlaybackInfo",
        string method = "POST")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Items["item"] = item;

        if (client is not null)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-Client", client)]));
        }

        var descriptor = new ControllerActionDescriptor
        {
            AttributeRouteInfo = new AttributeRouteInfo { Template = route },
        };

        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?> { ["itemId"] = item?.Id ?? Guid.NewGuid() },
            controller: new object());
    }

    private static ILibraryManager LibraryOf(BaseItem? item)
    {
        var proxy = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
        ((LibraryProxy)(object)proxy).Item = item;
        return proxy;
    }

    /// <summary>
    /// An analysis that answers at once, and counts whether it was asked at all.
    /// </summary>
    private sealed class StubAnalyser(H264EntryPointEvidence evidence) : IRecordingAnalyser
    {
        public int Calls { get; private set; }

        public bool Throws { get; init; }

        public Task<RecordingAnalysis> AnalyseAsync(string recordingId, bool recordingHasFinished, CancellationToken cancellationToken)
        {
            Calls++;

            if (Throws)
            {
                throw new InvalidOperationException("TVHeadend was not reachable.");
            }

            Assert.Equal(RecordingId, recordingId);
            return Task.FromResult(new RecordingAnalysis(null, null, evidence));
        }
    }

    /// <summary>
    /// Jellyfin's library, reduced to the two questions the filter asks it.
    /// </summary>
    public class LibraryProxy : DispatchProxy
    {
        /// <summary>
        /// The identifier Jellyfin's channel manager derives for the recordings channel, computed
        /// the way this proxy computes every identifier.
        /// </summary>
        public static Guid RecordingsChannelIdentifier { get; } =
            Hash("Channel " + TvheadendItems.RecordingsChannelName, typeof(Channel));

        internal BaseItem? Item { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ILibraryManager.GetNewItemId):
                    return Hash((string)args![0]!, (Type)args[1]!);

                case nameof(ILibraryManager.GetItemById):
                    return Item is not null && Item.Id.Equals((Guid)args![0]!) ? Item : null;

                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }

        private static Guid Hash(string key, Type type)
            => new(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(key + "|" + type.FullName)));
    }
}
