using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Tvheadend.Catalogs;
using Xunit;

namespace TVHeadEnd.Tests.LiveTv;

/// <summary>
/// The server's own grouping of its channels, passed through to Jellyfin.
/// </summary>
/// <remarks>
/// TVHeadend sends tags and channels as two separate things: a tag carries its name, a channel
/// carries the numbers of the tags it is in. Keeping them apart is what makes a rename on the
/// server show up everywhere at once, and it is also the thing a naive implementation gets wrong
/// by copying the name onto every channel that referenced it.
/// </remarks>
public class ChannelTagTests
{
    [Fact]
    public void ATagIsRemembered()
    {
        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));

        Assert.Equal(1, tags.Count);
        Assert.Equal(["TV channels"], tags.Resolve([1]));
    }

    [Fact]
    public void ATagUpdateRenamesIt()
    {
        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));
        tags.AddOrUpdate(HtspMessage.Create("tagUpdate").Set("tagId", 1).Set("tagName", "Fernsehen"));

        Assert.Equal(["Fernsehen"], tags.Resolve([1]));
    }

    [Fact]
    public void AnUpdateThatCarriesNoNameKeepsTheOneItHas()
    {
        // Measured on the live server: during the initial sync TVHeadend sends every tag twice,
        // and the second round carries the member list. Taking the name from that unconditionally
        // would blank every tag name moments after learning it.
        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));
        tags.AddOrUpdate(HtspMessage.Create("tagUpdate").Set("tagId", 1).Set("members", [(long)5089966]));

        Assert.Equal(["TV channels"], tags.Resolve([1]));
    }

    [Fact]
    public void ADeletedTagStopsBeingResolved()
    {
        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));
        tags.Remove(HtspMessage.Create("tagDelete").Set("tagId", 1));

        Assert.Empty(tags.Resolve([1]));
        Assert.Equal(0, tags.Count);
    }

    [Fact]
    public void AReconnectStartsWithNoTags()
    {
        // The catalogs belong to one connection. A reconnect re-announces everything, and merging
        // two pictures of the world would leave tags nobody has any more.
        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));
        tags.Clear();

        Assert.Equal(0, tags.Count);
        Assert.Empty(tags.Resolve([1]));
    }

    [Fact]
    public void AChannelKeepsTheTagsItWasAnnouncedWith()
    {
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1, 2]));

        Assert.Equal([1, 2], channels.Get("5089966")!.TagIds);
    }

    [Fact]
    public void RenamingAChannelDoesNotEmptyItsTags()
    {
        // A channelUpdate mentions only what changed. Reading a missing tags field as "no tags"
        // would strip every channel of its groups the first time one was renamed.
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1, 2]));
        channels.AddOrUpdate(HtspMessage.Create("channelUpdate")
            .Set("channelId", 5089966)
            .Set("channelName", "Das Erste HD"));

        Assert.Equal([1, 2], channels.Get("5089966")!.TagIds);
        Assert.Equal("Das Erste HD", channels.Get("5089966")!.Name);
    }

    [Fact]
    public void AnUpdateThatDoesStateTagsReplacesThem()
    {
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1, 2]));
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [3]));

        Assert.Equal([3], channels.Get("5089966")!.TagIds);
    }

    [Fact]
    public void AChannelTakenOutOfEveryTagIsInNone()
    {
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1, 2]));
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: []));

        Assert.Empty(channels.Get("5089966")!.TagIds);
    }

    [Fact]
    public void TheNamesReachTheChannelJellyfinIsOffered()
    {
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1, 2]));

        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));
        tags.AddOrUpdate(Tag(2, "HD"));

        var offered = channels.ToChannelInfos(tags).Single();

        Assert.Equal(["TV channels", "HD"], offered.Tags);
    }

    [Fact]
    public void ARenameShowsUpWithoutAnyChannelBeingTouched()
    {
        // The whole reason the two are kept apart. Nothing about the channel is rewritten and the
        // next listing says the new name.
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1]));

        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));
        var before = channels.Get("5089966");

        tags.AddOrUpdate(Tag(1, "Fernsehen"));

        Assert.Equal(["Fernsehen"], channels.ToChannelInfos(tags).Single().Tags);
        Assert.Same(before, channels.Get("5089966"));
    }

    [Fact]
    public void TwoChannelsShareOneTag()
    {
        var channels = Catalog();
        channels.AddOrUpdate(Channel(1, "Das Erste", tags: [1]));
        channels.AddOrUpdate(Channel(2, "ZDF", tags: [1]));

        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));

        Assert.All(channels.ToChannelInfos(tags), channel => Assert.Equal(["TV channels"], channel.Tags));
    }

    [Fact]
    public void ATagNobodyAnnouncedIsLeftOut()
    {
        // Its number is the server's bookkeeping and means nothing to a viewer.
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1, 99]));

        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));

        Assert.Equal(["TV channels"], channels.ToChannelInfos(tags).Single().Tags);
    }

    [Fact]
    public void ATagWithNoNameIsLeftOut()
    {
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1, 2]));

        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));
        tags.AddOrUpdate(Tag(2, "   "));

        Assert.Equal(["TV channels"], channels.ToChannelInfos(tags).Single().Tags);
    }

    [Fact]
    public void OneLabelIsListedOnce()
    {
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: [1, 2]));

        var tags = new ChannelTagCatalog();
        tags.AddOrUpdate(Tag(1, "TV channels"));
        tags.AddOrUpdate(Tag(2, "tv channels"));

        Assert.Equal(["TV channels"], channels.ToChannelInfos(tags).Single().Tags);
    }

    [Fact]
    public void AChannelInNoTagsIsOfferedWithNone()
    {
        // And is still offered. Nothing is invented from the service type, the channel kind or
        // the name to fill the gap.
        var channels = Catalog();
        channels.AddOrUpdate(Channel(5089966, "Das Erste", tags: []));

        var offered = channels.ToChannelInfos(new ChannelTagCatalog()).Single();

        Assert.Empty(offered.Tags);
        Assert.Equal("Das Erste", offered.Name);
    }

    private static ChannelCatalog Catalog() => new(NullLogger<ChannelCatalog>.Instance);

    private static HtspMessage Tag(int id, string name)
        => HtspMessage.Create("tagAdd").Set("tagId", id).Set("tagName", name);

    private static HtspMessage Channel(int id, string name, long[] tags)
        => HtspMessage.Create("channelAdd")
            .Set("channelId", id)
            .Set("channelName", name)
            .Set("channelNumber", 1)
            .Set("services", [new HtspMessage().Set("type", "HDTV")])
            .Set("tags", tags);
}
