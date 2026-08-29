namespace TVHeadEnd.Tvheadend.Catalogs;

/// <summary>
/// One channel tag as TVHeadend announced it.
/// </summary>
/// <remarks>
/// A tag is the server's own grouping of channels -- "TV channels", "Radio", "HD", whatever the
/// person running it made. Channels reference it by number and it is stored once, so renaming it
/// on the server renames it everywhere without a single channel record being touched.
/// </remarks>
/// <param name="Id">The tag identifier, the number channels reference it by.</param>
/// <param name="Name">The name meant to be shown.</param>
public sealed record ChannelTag(int Id, string? Name);
