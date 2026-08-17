using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Media;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Media;

public sealed class ChannelFormatPreAnalyzerTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "tvh-pre-" + Guid.NewGuid().ToString("N"));
    private readonly ChannelMediaDescriptorStore _store;
    private readonly ChannelFormatPreAnalyzer _analyzer;

    public ChannelFormatPreAnalyzerTests()
    {
        Directory.CreateDirectory(_dataPath);
        _store = new ChannelMediaDescriptorStore(new StubPaths(_dataPath), NullLogger.Instance);
        _analyzer = new ChannelFormatPreAnalyzer(_store, NullLogger.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OnlyChannelsWithoutACurrentDescriptionAreAnalyzed()
    {
        _store.Record(Descriptor("known", "pass"));
        var seen = new ConcurrentBag<string>();

        var analysed = await _analyzer.Run(
            ["known", "unknown-a", "unknown-b"],
            "pass",
            (channelId, _) =>
            {
                seen.Add(channelId);
                return Task.FromResult<ChannelMediaDescriptor?>(Descriptor(channelId, "pass"));
            },
            CancellationToken.None);

        Assert.Equal(2, analysed);
        Assert.DoesNotContain("known", seen);
        Assert.Contains("unknown-a", seen);
        Assert.Contains("unknown-b", seen);
    }

    [Fact]
    public async Task AChangedNativeProfileMakesEveryChannelStaleAgain()
    {
        _store.Record(Descriptor("one", "pass"));

        var analysed = await _analyzer.Run(
            ["one"],
            "webtv-h264",
            (channelId, _) => Task.FromResult<ChannelMediaDescriptor?>(Descriptor(channelId, "webtv-h264")),
            CancellationToken.None);

        Assert.Equal(1, analysed);
    }

    [Fact]
    public async Task OneChannelAtATime()
    {
        // Each analysis occupies a tuner. Running them together would take live TV away from
        // whoever is watching.
        var concurrent = 0;
        var peak = 0;

        await _analyzer.Run(
            ["a", "b", "c", "d"],
            "pass",
            async (channelId, token) =>
            {
                var now = Interlocked.Increment(ref concurrent);
                peak = Math.Max(peak, now);
                await Task.Delay(5, token);
                Interlocked.Decrement(ref concurrent);
                return Descriptor(channelId, "pass");
            },
            CancellationToken.None);

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task ACancelledRefreshStopsTheAnalysis()
    {
        using var cancellation = new CancellationTokenSource();
        var started = 0;

        var analysed = await _analyzer.Run(
            ["a", "b", "c", "d"],
            "pass",
            async (channelId, _) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                {
                    await cancellation.CancelAsync();
                }

                return Descriptor(channelId, "pass");
            },
            cancellation.Token);

        Assert.True(analysed < 4);
        Assert.True(started <= 3);
    }

    [Fact]
    public async Task AChannelThatCannotBeAnalyzedIsSkippedRatherThanFailingTheRefresh()
    {
        var analysed = await _analyzer.Run(
            ["broken", "fine"],
            "pass",
            (channelId, _) => channelId == "broken"
                ? throw new InvalidOperationException("no tuner")
                : Task.FromResult<ChannelMediaDescriptor?>(Descriptor(channelId, "pass")),
            CancellationToken.None);

        Assert.Equal(1, analysed);
        Assert.Null(_store.Get("broken", "pass"));
        Assert.NotNull(_store.Get("fine", "pass"));
    }

    [Fact]
    public async Task AnUnusableResultIsNotStored()
    {
        var analysed = await _analyzer.Run(
            ["empty"],
            "pass",
            (_, _) => Task.FromResult<ChannelMediaDescriptor?>(null),
            CancellationToken.None);

        Assert.Equal(0, analysed);
        Assert.Null(_store.Get("empty", "pass"));
    }

    private static ChannelMediaDescriptor Descriptor(string channelId, string nativeProfile)
        => new()
        {
            ChannelId = channelId,
            NativeProfile = nativeProfile,
            Container = "mpegts,ts",
            VideoStreamType = 0x1B,
            RandomAccess = H264RandomAccessKind.Idr,
            IsTransportStream = true,
            Streams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }],
        };

    private sealed class StubPaths(string path) : IApplicationPaths
    {
        public string ProgramDataPath => path;

        public string WebPath => path;

        public string ProgramSystemPath => path;

        public string DataPath => path;

        public string ImageCachePath => path;

        public string PluginsPath => path;

        public string PluginConfigurationsPath => path;

        public string LogDirectoryPath => path;

        public string ConfigurationDirectoryPath => path;

        public string SystemConfigurationFilePath => Path.Combine(path, "system.xml");

        public string CachePath { get; set; } = path;

        public string TempDirectory => path;

        public string TrickplayPath => path;

        public string BackupPath => path;

        public string VirtualDataPath => path;

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }

        public void MakeSanityCheckOrThrow()
        {
        }
    }
}
