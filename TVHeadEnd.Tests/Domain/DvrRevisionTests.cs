using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Domain;
using TVHeadEnd.Tvheadend.Catalogs;
using Xunit;

namespace TVHeadEnd.Tests.Domain;

/// <summary>
/// When the DVR catalog counts a change.
/// </summary>
/// <remarks>
/// The revision is what the recordings channel's cache key is built on, so it is the answer to
/// "has anything a listing would show moved". TVHeadend sends a <c>dvrEntryUpdate</c> for a
/// running recording every few seconds carrying only its statistics -- bytes written, disk space,
/// errors counted -- none of which this plugin reads. Counting those discarded and rebuilt the
/// whole recordings listing every few seconds for as long as anything was recording.
/// </remarks>
public class DvrRevisionTests
{
    [Fact]
    public void AStatsOnlyUpdateChangesNothing()
    {
        var catalog = Catalog(Entry(1, "recording", title: "Tatort"));
        var before = catalog.Revision;

        catalog.Update(StatsOnly(1));

        Assert.Equal(before, catalog.Revision);
        Assert.Equal("Tatort", catalog.Find("1")!.Title);
    }

    [Fact]
    public void AnUpdateRepeatingWhatIsAlreadyKnownChangesNothing()
    {
        // TVHeadend also re-sends whole entries. An update that says exactly what the catalog
        // already holds is not news, however much of it there is.
        var catalog = Catalog(Entry(1, "recording", title: "Tatort"));
        var before = catalog.Revision;

        catalog.Update(Entry(1, "recording", title: "Tatort"));

        Assert.Equal(before, catalog.Revision);
    }

    [Fact]
    public void AStateChangeCounts()
    {
        var catalog = Catalog(Entry(1, "recording", title: "Tatort"));
        var before = catalog.Revision;

        var update = new HtspMessage();
        update.Set("id", 1L);
        update.Set("state", "completed");
        catalog.Update(update);

        Assert.Equal(before + 1, catalog.Revision);
        Assert.Equal(DvrState.Completed, catalog.Find("1")!.State);
    }

    [Fact]
    public void AFileChangeCounts()
    {
        // The update that matters most: a recording finishing brings the file's stop, and with it
        // the real runtime. If that did not count, the listing would keep the length it had while
        // the file was still growing.
        var catalog = Catalog(WithFiles(Entry(1, "recording"), File(Opened, stop: null)));
        var before = catalog.Revision;

        catalog.Update(WithFiles(Entry(1, "recording"), File(Opened, Closed)));

        Assert.Equal(before + 1, catalog.Revision);
        Assert.Equal(Closed, catalog.Find("1")!.PlayableFile!.StopUtc);
    }

    [Fact]
    public void TheSameFileListSentAgainAsAFreshInstanceChangesNothing()
    {
        // The reason the entries are compared element by element. A record compares an
        // IReadOnlyList by reference, and every message parsed builds a new list -- so an
        // unchanged files block would have counted as a change every single time it arrived.
        var catalog = Catalog(WithFiles(Entry(1, "recording"), File(Opened, Closed)));
        var before = catalog.Revision;

        catalog.Update(WithFiles(Entry(1, "recording"), File(Opened, Closed)));

        Assert.Equal(before, catalog.Revision);
    }

    [Fact]
    public void AFileAppearingAtTheEndCounts()
    {
        // Which file is last decides the one TVHeadend serves, so a list that grew is a listing
        // that changed even when every file already in it is untouched.
        var catalog = Catalog(WithFiles(Entry(1, "recording"), File(Opened, Closed)));
        var before = catalog.Revision;

        catalog.Update(WithFiles(Entry(1, "recording"), File(Opened, Closed), File(Closed, Closed.AddMinutes(10))));

        Assert.Equal(before + 1, catalog.Revision);
        Assert.Equal(Closed.AddMinutes(10), catalog.Find("1")!.PlayableFile!.StopUtc);
    }

    [Theory]
    [InlineData("title", "Polizeiruf")]
    [InlineData("description", "A different overview")]
    [InlineData("image", "imagecache/99")]
    [InlineData("fanartImage", "imagecache/100")]
    public void AMetadataChangeCounts(string field, string value)
    {
        var catalog = Catalog(Entry(1, "recording", title: "Tatort"));
        var before = catalog.Revision;

        var update = new HtspMessage();
        update.Set("id", 1L);
        update.Set(field, value);
        catalog.Update(update);

        Assert.Equal(before + 1, catalog.Revision);
    }

    [Fact]
    public void AContentTypeChangeCounts()
    {
        var catalog = Catalog(Entry(1, "recording", title: "Tatort"));
        var before = catalog.Revision;

        var update = new HtspMessage();
        update.Set("id", 1L);
        update.Set("contentType", 0x11L);
        catalog.Update(update);

        Assert.Equal(before + 1, catalog.Revision);
        Assert.Equal(0x11, catalog.Find("1")!.ContentType);
    }

    [Fact]
    public void AnUpdateForAnEntryNobodyAnnouncedIsTakenAndCounted()
    {
        // Announced or not, it is new to this connection, and a listing without it would be
        // missing a recording.
        var catalog = Catalog();
        var before = catalog.Revision;

        catalog.Update(Entry(7, "completed", title: "Tagesschau"));

        Assert.Equal(before + 1, catalog.Revision);
        Assert.Equal("Tagesschau", catalog.Find("7")!.Title);
    }

    [Fact]
    public void ManyStatsUpdatesInARowStillChangeNothing()
    {
        // The shape of the real traffic: one running recording, an update every few seconds, for
        // as long as it runs. Each one used to discard the whole recordings listing.
        var catalog = Catalog(Entry(1, "recording", title: "Tatort"));
        var before = catalog.Revision;

        for (var i = 0; i < 50; i++)
        {
            catalog.Update(StatsOnly(1));
        }

        Assert.Equal(before, catalog.Revision);
    }

    [Fact]
    public void TwoEntriesReadFromTheSameFilesCompareAsTheSame()
    {
        // Directly on the entry, because that is where the comparison lives.
        var first = DvrEntry.FromMessage(WithFiles(Entry(1, "completed", title: "Tatort"), File(Opened, Closed)))!;
        var second = DvrEntry.FromMessage(WithFiles(Entry(1, "completed", title: "Tatort"), File(Opened, Closed)))!;

        Assert.NotSame(first.Files, second.Files);
        Assert.True(first.HasSameContentAs(second));

        var different = DvrEntry.FromMessage(WithFiles(Entry(1, "completed", title: "Tatort"), File(Opened, Closed.AddSeconds(1))))!;

        Assert.False(first.HasSameContentAs(different));
    }

    [Fact]
    public void AnEntryWithNoFilesComparesAsTheSameAsAnotherWithNone()
    {
        // Two separately created empty lists are not the same reference, which is exactly the trap
        // record equality falls into.
        var first = DvrEntry.FromMessage(Entry(1, "scheduled", title: "Tatort"))!;
        var second = DvrEntry.FromMessage(Entry(1, "scheduled", title: "Tatort"))!;

        // Both are the empty singleton here, which is the one case reference equality would have
        // got right by accident. What matters is that the comparison does not depend on that.
        Assert.Empty(first.Files);
        Assert.True(first.HasSameContentAs(second));
    }

    private static readonly DateTime Opened = new(2026, 8, 29, 20, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime Closed = new(2026, 8, 29, 21, 45, 0, DateTimeKind.Utc);

    private static DvrCatalog Catalog(params HtspMessage[] entries)
    {
        var catalog = new DvrCatalog(NullLogger<DvrCatalog>.Instance);
        foreach (var entry in entries)
        {
            catalog.Add(entry);
        }

        return catalog;
    }

    private static HtspMessage Entry(int id, string state, string? title = null)
    {
        var message = new HtspMessage();
        message.Set("id", (long)id);
        message.Set("state", state);

        if (title is not null)
        {
            message.Set("title", title);
        }

        return message;
    }

    /// <summary>
    /// What TVHeadend sends every few seconds while a recording runs: the identifier and its
    /// statistics, none of which a <see cref="DvrEntry"/> reads.
    /// </summary>
    private static HtspMessage StatsOnly(int id)
    {
        var message = new HtspMessage();
        message.Set("id", (long)id);
        message.Set("dataSize", 4_294_967_296L);
        message.Set("dataErrors", 0L);
        message.Set("errors", 0L);
        return message;
    }

    private static HtspMessage WithFiles(HtspMessage message, params HtspMessage[] files)
    {
        message.Set("files", (IEnumerable<HtspMessage>)files);
        return message;
    }

    private static HtspMessage File(DateTime start, DateTime? stop)
    {
        var file = new HtspMessage();
        file.Set("start", ToUnixTime(start));

        if (stop is { } closed)
        {
            file.Set("stop", ToUnixTime(closed));
        }

        return file;
    }

    private static long ToUnixTime(DateTime value)
        => ((DateTimeOffset)DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
