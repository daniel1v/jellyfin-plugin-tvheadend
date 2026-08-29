using System.Collections.Generic;

namespace TVHeadEnd.Tvheadend.Catalogs;

/// <summary>
/// One channel as TVHeadend announced it.
/// </summary>
/// <param name="Id">The HTSP channel identifier, an integer the server assigns.</param>
/// <param name="Uuid">
/// The channel's stable identity, which is what the HTTP API addresses it by. The bridge between
/// the two halves of this plugin: HTSP names a channel by number, the API by this.
/// </param>
/// <param name="Name">The channel name.</param>
/// <param name="Number">The channel number, majors and minors combined.</param>
/// <param name="Icon">The icon reference, absolute or relative to the web root.</param>
/// <param name="ServiceType">The type of the first mapped service, such as "hdtv" or "radio".</param>
/// <param name="TagIds">
/// The tags the server has put this channel in, by number. Only the reference is kept: what a tag
/// is called lives in <see cref="ChannelTagCatalog"/>, so a tag renamed on the server is renamed
/// for every channel at once without any of them being rewritten.
/// </param>
public sealed record TvheadendChannel(
    int Id,
    string? Uuid,
    string? Name,
    string? Number,
    string? Icon,
    string? ServiceType,
    IReadOnlyList<int> TagIds);
