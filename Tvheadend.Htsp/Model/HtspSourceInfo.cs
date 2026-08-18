using System;
using System.Collections.Generic;
using System.Linq;
using Tvheadend.Htsp.Protocol;

namespace Tvheadend.Htsp.Model;

/// <summary>
/// Where a subscribed stream is physically coming from.
/// </summary>
/// <remarks>
/// <para>
/// Exactly the fields <c>htsp_subscription_start</c> puts in its <c>sourceinfo</c> map, under
/// their own names. The three UUIDs are stable identities; the rest are display strings the
/// server composes for people to read, and <see cref="Adapter"/>, <see cref="Mux"/>,
/// <see cref="Network"/>, <see cref="NetworkType"/>, <see cref="Provider"/>,
/// <see cref="Service"/> and <see cref="SatellitePosition"/> are all withheld from a user whose
/// account carries the anonymise right.
/// </para>
/// <para>
/// <see cref="Service"/> is the DVB service name, not a UUID. Pairing it with
/// <see cref="MuxUuid"/> identifies a service exactly, which is what lets the two halves of a
/// live stream be proven to be the same one.
/// </para>
/// </remarks>
/// <param name="AdapterUuid">The tuner's identity.</param>
/// <param name="MuxUuid">The multiplex's identity.</param>
/// <param name="NetworkUuid">The network's identity.</param>
/// <param name="Adapter">The tuner's display name.</param>
/// <param name="Mux">The multiplex's display name.</param>
/// <param name="Network">The network's name.</param>
/// <param name="NetworkType">The network's type, such as DVB-S or IPTV.</param>
/// <param name="Provider">The broadcaster.</param>
/// <param name="Service">The DVB service name.</param>
/// <param name="SatellitePosition">The orbital position, for a satellite network.</param>
public sealed record HtspSourceInfo(
    string? AdapterUuid,
    string? MuxUuid,
    string? NetworkUuid,
    string? Adapter,
    string? Mux,
    string? Network,
    string? NetworkType,
    string? Provider,
    string? Service,
    string? SatellitePosition)
{
    /// <summary>
    /// Gets a value indicating whether enough is known to identify the service this came from.
    /// </summary>
    public bool IdentifiesService => !string.IsNullOrEmpty(MuxUuid) && !string.IsNullOrEmpty(Service);

    /// <summary>
    /// Reads a <c>sourceinfo</c> map.
    /// </summary>
    /// <param name="message">The map, or <see langword="null"/> when the message carried none.</param>
    /// <returns>The parsed source information.</returns>
    public static HtspSourceInfo From(HtspMessage? message)
    {
        if (message is null)
        {
            return new HtspSourceInfo(null, null, null, null, null, null, null, null, null, null);
        }

        return new HtspSourceInfo(
            message.GetString("adapter_uuid"),
            message.GetString("mux_uuid"),
            message.GetString("network_uuid"),
            message.GetString("adapter"),
            message.GetString("mux"),
            message.GetString("network"),
            message.GetString("network_type"),
            message.GetString("provider"),
            message.GetString("service"),
            message.GetString("satpos"));
    }

    /// <summary>
    /// Renders the identifying parts for a log line.
    /// </summary>
    /// <returns>A short description.</returns>
    public override string ToString()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Service))
        {
            parts.Add("service=" + Service);
        }

        if (!string.IsNullOrEmpty(Mux))
        {
            parts.Add("mux=" + Mux);
        }

        if (!string.IsNullOrEmpty(MuxUuid))
        {
            parts.Add("mux_uuid=" + MuxUuid);
        }

        if (!string.IsNullOrEmpty(Adapter))
        {
            parts.Add("adapter=" + Adapter);
        }

        return parts.Count == 0 ? "<no source information>" : string.Join(" ", parts);
    }
}
