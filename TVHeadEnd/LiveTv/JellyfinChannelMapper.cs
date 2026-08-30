using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using TVHeadEnd.Tvheadend.Catalogs;

namespace TVHeadEnd.LiveTv;

/// <summary>
/// Describes the channels TVHeadend announced as the channels Jellyfin offers.
/// </summary>
/// <remarks>
/// <para>
/// One classification and one only. What kind of channel this is decides two separate things --
/// whether it is offered at all and how its stream is read -- and the two must never be able to
/// disagree, so both come through <see cref="ResolveChannelType"/>. A channel offered as
/// television whose stream is then read as radio is a channel that plays nothing and reports
/// nothing.
/// </para>
/// <para>
/// The service type is TVHeadend's word for it. Everything this plugin knows about a channel's
/// kind comes from there and from the one configured answer for the services TVHeadend itself
/// calls "other" -- nothing is inferred from a name, a number or a tag.
/// </para>
/// </remarks>
public static class JellyfinChannelMapper
{
    /// <summary>
    /// The configured treatment a channel gets when nothing is configured.
    /// </summary>
    private const string IgnoreOtherServices = "Ignore";

    /// <summary>
    /// Describes the channels Jellyfin should offer.
    /// </summary>
    /// <param name="channels">The channels TVHeadend has announced.</param>
    /// <param name="tags">The channel tags, for naming the ones each channel references.</param>
    /// <param name="typeForOther">How to treat a service TVHeadend tags "other".</param>
    /// <returns>The channels Jellyfin should offer.</returns>
    public static IReadOnlyList<ChannelInfo> ToChannelInfos(
        IReadOnlyList<TvheadendChannel> channels,
        ChannelTagCatalog tags,
        string? typeForOther)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(tags);

        var result = new List<ChannelInfo>(channels.Count);
        foreach (var channel in channels)
        {
            if (string.IsNullOrEmpty(channel.Name))
            {
                continue;
            }

            var type = ResolveChannelType(channel.ServiceType, typeForOther);
            if (type is null)
            {
                continue;
            }

            result.Add(new ChannelInfo
            {
                Id = channel.Id.ToString(CultureInfo.InvariantCulture),
                Name = channel.Name,
                Number = channel.Number,
                ChannelType = type.Value,
                IsHD = channel.ServiceType is not null
                    && !string.Equals(channel.ServiceType, "sdtv", StringComparison.OrdinalIgnoreCase)
                    && type == ChannelType.TV,

                // The server's own grouping of its channels, passed on as the server states it.
                // Nothing is added here from the service type, the channel kind or the name --
                // those are already published as what they are, and a tag invented from one would
                // be this plugin putting words in the server's mouth.
                Tags = [.. tags.Resolve(channel.TagIds)],

                ImagePath = string.Empty,
            });
        }

        return result;
    }

    /// <summary>
    /// Gets what kind of channel this is.
    /// </summary>
    /// <remarks>
    /// The one thing the transport stream cannot state. A program map with no video describes a
    /// radio service completely and a television channel not at all, and only the channel list
    /// knows which of the two arrived. A channel that is not in the list, or whose service type
    /// TVHeadend never announced, is treated as television: that is what all but a handful of
    /// channels are, and it is the reading under which a missing video stream is reported rather
    /// than quietly accepted.
    /// </remarks>
    /// <param name="channel">The channel, if the catalogue knows it.</param>
    /// <param name="typeForOther">How to treat a service TVHeadend tags "other".</param>
    /// <returns>The channel kind.</returns>
    public static ChannelType ChannelTypeFor(TvheadendChannel? channel, string? typeForOther)
        => ResolveChannelType(channel?.ServiceType, typeForOther) ?? ChannelType.TV;

    /// <summary>
    /// Reads TVHeadend's service type as a kind of channel, or as one not to offer at all.
    /// </summary>
    /// <param name="serviceType">The type of the channel's first mapped service.</param>
    /// <param name="typeForOther">How to treat a service TVHeadend tags "other".</param>
    /// <returns>The kind, or <see langword="null"/> where the channel is not offered.</returns>
    private static ChannelType? ResolveChannelType(string? serviceType, string? typeForOther)
        => serviceType?.ToLowerInvariant() switch
        {
            "radio" => ChannelType.Radio,
            "sdtv" or "hdtv" or "fhdtv" or "uhdtv" => ChannelType.TV,
            "other" => (typeForOther ?? IgnoreOtherServices).ToLowerInvariant() switch
            {
                "tv" => ChannelType.TV,
                "radio" => ChannelType.Radio,
                _ => null,
            },
            _ => null,
        };
}
