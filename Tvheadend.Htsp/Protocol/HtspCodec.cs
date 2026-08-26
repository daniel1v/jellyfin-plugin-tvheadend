using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Tvheadend.Htsp.Protocol;

/// <summary>
/// Reads and writes the HTSMSG binary encoding.
/// </summary>
/// <remarks>
/// <para>
/// A message is a four byte big-endian body length followed by that many bytes of fields. Each
/// field is a one byte type, a one byte name length, a four byte big-endian data length, the
/// name, and the data. Maps and lists nest the same encoding; a list is a map whose fields
/// carry no name.
/// </para>
/// <para>
/// Integers are the one place the encoding is not big-endian: TVHeadend writes them as unsigned
/// little-endian with trailing zero bytes removed, so zero occupies no bytes at all and a
/// negative number occupies all eight. Decoding zero-extends, which is what makes that
/// asymmetry round-trip.
/// </para>
/// </remarks>
public static class HtspCodec
{
    /// <summary>
    /// The largest message this client will accept.
    /// </summary>
    /// <remarks>
    /// A length prefix is four bytes of attacker- or accident-controlled data pointing at an
    /// allocation. TVHeadend's own limit for a message it will assemble is well under this, and
    /// nothing this client subscribes to approaches it, so a larger claim is a damaged stream
    /// rather than a big message and is refused before anything is allocated for it.
    /// </remarks>
    public const int MaximumMessageLength = 8 * 1024 * 1024;

    private const byte TypeMap = 1;
    private const byte TypeInt64 = 2;
    private const byte TypeString = 3;
    private const byte TypeBinary = 4;
    private const byte TypeList = 5;
    private const byte TypeDouble = 6;
    private const byte TypeBoolean = 7;
    private const byte TypeUuid = 8;

    private const int FieldHeaderLength = 6;

    /// <summary>
    /// The length of the frame header that precedes every message body.
    /// </summary>
    public const int FrameHeaderLength = 4;

    /// <summary>
    /// Encodes a message, including its frame header.
    /// </summary>
    /// <param name="message">The message to encode.</param>
    /// <returns>The bytes to write to the socket.</returns>
    public static byte[] Encode(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = new MemoryStream();
        WriteFields(body, message);

        var length = checked((int)body.Length);
        var frame = new byte[FrameHeaderLength + length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)length);
        body.GetBuffer().AsSpan(0, length).CopyTo(frame.AsSpan(FrameHeaderLength));
        return frame;
    }

    /// <summary>
    /// Reads the body length out of a frame header.
    /// </summary>
    /// <param name="header">Four bytes of frame header.</param>
    /// <returns>The length of the body that follows.</returns>
    /// <exception cref="HtspProtocolException">The length is not one this client will honour.</exception>
    public static int ReadBodyLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < FrameHeaderLength)
        {
            throw new HtspProtocolException("An HTSP frame header was shorter than four bytes.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaximumMessageLength)
        {
            throw new HtspProtocolException(string.Create(
                CultureInfo.InvariantCulture,
                $"An HTSP frame announced {length} bytes, past the {MaximumMessageLength} byte limit. The stream is out of step with the protocol."));
        }

        return (int)length;
    }

    /// <summary>
    /// Decodes a message body, without its frame header.
    /// </summary>
    /// <param name="body">The body bytes.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="HtspProtocolException">The body is not a well formed HTSMSG map.</exception>
    public static HtspMessage Decode(ReadOnlySpan<byte> body) => HtspMessage.FromDecoded(DecodeFields(body, false));

    private static Dictionary<string, object> DecodeFields(ReadOnlySpan<byte> body, bool isList)
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        var anonymousIndex = 0;

        while (!body.IsEmpty)
        {
            if (body.Length < FieldHeaderLength)
            {
                throw new HtspProtocolException("An HTSMSG field header was truncated.");
            }

            var type = body[0];
            var nameLength = body[1];
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(body[2..]);
            body = body[FieldHeaderLength..];

            // Both lengths come off the wire, so the sum is checked as a long before either is
            // used as a slice length.
            if ((long)nameLength + dataLength > body.Length)
            {
                throw new HtspProtocolException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"An HTSMSG field claimed {nameLength} name and {dataLength} data bytes but only {body.Length} remain."));
            }

            var name = nameLength == 0
                ? anonymousIndex++.ToString(CultureInfo.InvariantCulture)
                : Encoding.UTF8.GetString(body[..nameLength]);
            body = body[nameLength..];

            var data = body[..(int)dataLength];
            body = body[(int)dataLength..];

            // A list element carries no name, so a named field inside one -- or an unnamed field
            // inside a map -- means the two sides disagree about the shape. Neither is worth
            // rejecting the message over; the positional name keeps list order intact either way.
            fields[name] = DecodeValue(type, data, name);
        }

        _ = isList;
        return fields;
    }

    private static object DecodeValue(byte type, ReadOnlySpan<byte> data, string name)
        => type switch
        {
            TypeString => Encoding.UTF8.GetString(data),
            TypeBinary => data.ToArray(),
            TypeInt64 => DecodeInt64(data),
            TypeBoolean => data.Length == 1 && data[0] != 0 ? 1L : 0L,
            TypeMap => HtspMessage.FromDecoded(DecodeFields(data, false)),
            TypeList => DecodeList(data),
            TypeUuid => data.ToArray(),

            // Never sent by any method this client calls, but a message carrying one elsewhere
            // must not take down the connection.
            TypeDouble => data.ToArray(),
            _ => throw new HtspProtocolException(string.Create(
                CultureInfo.InvariantCulture,
                $"HTSMSG field '{name}' has unknown type {type}.")),
        };

    private static long DecodeInt64(ReadOnlySpan<byte> data)
    {
        if (data.Length > sizeof(ulong))
        {
            throw new HtspProtocolException(string.Create(
                CultureInfo.InvariantCulture,
                $"An HTSMSG integer occupied {data.Length} bytes, more than eight."));
        }

        ulong value = 0;
        for (var index = data.Length - 1; index >= 0; index--)
        {
            value = (value << 8) | data[index];
        }

        return unchecked((long)value);
    }

    private static List<object> DecodeList(ReadOnlySpan<byte> data)
    {
        // Elements are unnamed, so the decoder named them by position; the list is rebuilt in
        // that order.
        var decoded = DecodeFields(data, true);
        var elements = new List<object>(decoded.Count);
        for (var index = 0; index < decoded.Count; index++)
        {
            if (!decoded.TryGetValue(index.ToString(CultureInfo.InvariantCulture), out var element))
            {
                break;
            }

            elements.Add(element);
        }

        return elements;
    }

    private static void WriteFields(MemoryStream destination, HtspMessage message)
    {
        foreach (var field in message.EnumerateFields())
        {
            WriteField(destination, field.Key, field.Value);
        }
    }

    private static void WriteField(MemoryStream destination, string name, object value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > byte.MaxValue)
        {
            throw new HtspProtocolException(string.Create(
                CultureInfo.InvariantCulture,
                $"HTSMSG field name '{name}' is longer than 255 bytes."));
        }

        byte type;
        byte[] data;
        switch (value)
        {
            case string text:
                type = TypeString;
                data = Encoding.UTF8.GetBytes(text);
                break;
            case long number:
                type = TypeInt64;
                data = EncodeInt64(number);
                break;
            case byte[] binary:
                type = TypeBinary;
                data = binary;
                break;
            case HtspMessage map:
                type = TypeMap;
                data = EncodeNested(map);
                break;
            case IReadOnlyList<object> list:
                type = TypeList;
                data = EncodeList(list);
                break;
            default:
                throw new HtspProtocolException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"HTSMSG field '{name}' holds {value.GetType().Name}, which the protocol cannot carry."));
        }

        Span<byte> header = stackalloc byte[FieldHeaderLength];
        header[0] = type;
        header[1] = (byte)nameBytes.Length;
        BinaryPrimitives.WriteUInt32BigEndian(header[2..], (uint)data.Length);

        destination.Write(header);
        destination.Write(nameBytes);
        destination.Write(data);
    }

    private static byte[] EncodeNested(HtspMessage message)
    {
        var nested = new MemoryStream();
        WriteFields(nested, message);
        return nested.ToArray();
    }

    private static byte[] EncodeList(IReadOnlyList<object> list)
    {
        var nested = new MemoryStream();
        foreach (var element in list)
        {
            // A list element is a field with an empty name.
            WriteField(nested, string.Empty, element);
        }

        return nested.ToArray();
    }

    private static byte[] EncodeInt64(long value)
    {
        // Unsigned little-endian, shortest form. Zero occupies no bytes; a negative number
        // occupies all eight, which is what the zero-extending decoder needs to recover it.
        var bits = unchecked((ulong)value);
        var length = 0;
        for (var remaining = bits; remaining != 0; remaining >>= 8)
        {
            length++;
        }

        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)(bits >> (index * 8));
        }

        return data;
    }
}
