using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp.Protocol;

namespace TVHeadEnd.Tvheadend.Catalogs;

/// <summary>
/// The channels TVHeadend has announced over the metadata feed.
/// </summary>
public sealed class ChannelCatalog
{
    private readonly ILogger<ChannelCatalog> _logger;
    private readonly Dictionary<int, TvheadendChannel> _channels = [];
    private readonly object _gate = new();

    private string _typeForOther = "Ignore";

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelCatalog"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ChannelCatalog(ILogger<ChannelCatalog> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets how many channels are known.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _channels.Count;
            }
        }
    }

    /// <summary>
    /// Sets how a channel whose service is tagged "other" is treated.
    /// </summary>
    /// <param name="channelType">The configured treatment.</param>
    public void SetTypeForOther(string? channelType) => _typeForOther = channelType ?? "Ignore";

    /// <summary>
    /// Records a channel the server added or changed.
    /// </summary>
    /// <remarks>
    /// An update mentions only what changed, so it is merged onto the channel as it stood. The
    /// server also announces channels with no number, which its own web interface hides; they
    /// are kept out of the catalogue for the same reason.
    /// </remarks>
    /// <param name="message">The <c>channelAdd</c> or <c>channelUpdate</c> message.</param>
    public void AddOrUpdate(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var id = message.GetInt32("channelId");
        if (id is null)
        {
            return;
        }

        lock (_gate)
        {
            _channels.TryGetValue(id.Value, out var existing);

            var number = ReadNumber(message) ?? existing?.Number;
            if (number is null)
            {
                // A channel the server has not given a number to. Nothing to merge onto either,
                // so it is simply not offered.
                return;
            }

            _channels[id.Value] = new TvheadendChannel(
                id.Value,
                message.GetString("channelIdStr") ?? existing?.Uuid,
                message.GetString("channelName") ?? existing?.Name,
                number,
                message.GetString("channelIcon") ?? existing?.Icon,
                ReadServiceType(message) ?? existing?.ServiceType,
                ReadTagIds(message) ?? existing?.TagIds ?? []);
        }
    }

    /// <summary>
    /// Forgets a channel the server removed.
    /// </summary>
    /// <param name="message">The <c>channelDelete</c> message.</param>
    public void Remove(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.GetInt32("channelId") is { } id)
        {
            lock (_gate)
            {
                _channels.Remove(id);
            }
        }
    }

    /// <summary>
    /// Discards everything, for a connection that is starting over.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _channels.Clear();
        }
    }

    /// <summary>
    /// Gets one channel.
    /// </summary>
    /// <param name="channelId">The HTSP channel identifier, as Jellyfin passes it back.</param>
    /// <returns>The channel, or <see langword="null"/> when it is not known.</returns>
    public TvheadendChannel? Get(string? channelId)
    {
        if (!int.TryParse(channelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        lock (_gate)
        {
            return _channels.GetValueOrDefault(id);
        }
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
    /// <param name="channelId">The HTSP channel identifier, as Jellyfin passes it back.</param>
    /// <returns>The channel kind.</returns>
    public ChannelType GetChannelType(string? channelId)
        => ResolveChannelType(Get(channelId)?.ServiceType) ?? ChannelType.TV;

    /// <summary>
    /// Describes the channels for Jellyfin.
    /// </summary>
    /// <remarks>
    /// The tag catalogue is passed in rather than held, so that neither catalogue has to know the
    /// other exists. Names are looked up here, at the moment the description is built, which is
    /// what makes a tag renamed on the server show its new name without any channel being touched.
    /// </remarks>
    /// <param name="tags">The channel tags, for naming the ones each channel references.</param>
    /// <returns>The channels Jellyfin should offer.</returns>
    public IReadOnlyList<ChannelInfo> ToChannelInfos(ChannelTagCatalog tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        List<TvheadendChannel> snapshot;
        lock (_gate)
        {
            snapshot = [.. _channels.Values];
        }

        var result = new List<ChannelInfo>(snapshot.Count);
        foreach (var channel in snapshot)
        {
            if (string.IsNullOrEmpty(channel.Name))
            {
                continue;
            }

            var type = ResolveChannelType(channel.ServiceType);
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

    private ChannelType? ResolveChannelType(string? serviceType) => serviceType?.ToLowerInvariant() switch
    {
        "radio" => ChannelType.Radio,
        "sdtv" or "hdtv" or "fhdtv" or "uhdtv" => ChannelType.TV,
        "other" => _typeForOther.ToLowerInvariant() switch
        {
            "tv" => ChannelType.TV,
            "radio" => ChannelType.Radio,
            _ => null,
        },
        _ => null,
    };

    private static string? ReadNumber(HtspMessage message)
    {
        var major = message.GetInt32("channelNumber");
        if (major is not > 0)
        {
            return null;
        }

        var minor = message.GetInt32("channelNumberMinor");
        return minor is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}")
            : major.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string? ReadServiceType(HtspMessage message)
    {
        var services = message.GetMapList("services");
        return services.Count == 0 ? null : services[0].GetString("type");
    }

    /// <summary>
    /// Reads the tags the message states, or nothing at all where it states none.
    /// </summary>
    /// <remarks>
    /// The distinction between "no tags" and "nothing said about tags" is the whole point. An
    /// update that changes only a channel's name carries no <c>tags</c> field, and reading that as
    /// an empty list would strip every channel of its groups the first time one was renamed. A
    /// message that does carry the field replaces what was there, empty list included, because
    /// that is the server saying the channel is now in no tags at all.
    /// </remarks>
    private static IReadOnlyList<int>? ReadTagIds(HtspMessage message)
    {
        if (!message.Contains("tags"))
        {
            return null;
        }

        var tags = message.GetInt64List("tags");
        if (tags.Count == 0)
        {
            return [];
        }

        var ids = new List<int>(tags.Count);
        foreach (var tag in tags)
        {
            if (tag is >= int.MinValue and <= int.MaxValue)
            {
                ids.Add((int)tag);
            }
        }

        return ids;
    }
}
