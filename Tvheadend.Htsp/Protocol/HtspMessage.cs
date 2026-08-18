using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tvheadend.Htsp.Protocol;

/// <summary>
/// One HTSMSG map: the only shape an HTSP message ever takes on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Values are restricted to what the protocol can carry -- <see cref="string"/>,
/// <see cref="long"/>, <see cref="byte"/> arrays, nested <see cref="HtspMessage"/> maps and
/// lists of those. Anything else is refused where it is added rather than where it is
/// serialised, so a malformed request cannot be built at all.
/// </para>
/// <para>
/// Field access is deliberately total: every accessor either returns a value or reports that
/// the field was absent or of another type. HTSP omits fields freely -- many are conditional on
/// the negotiated version or on what a tuner happened to report -- so treating an absent field
/// as an error would make the common case the exceptional one.
/// </para>
/// </remarks>
public sealed class HtspMessage
{
    private readonly Dictionary<string, object> _fields;

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspMessage"/> class.
    /// </summary>
    public HtspMessage()
    {
        _fields = new Dictionary<string, object>(StringComparer.Ordinal);
    }

    private HtspMessage(Dictionary<string, object> fields)
    {
        _fields = fields;
    }

    /// <summary>
    /// Gets the number of fields.
    /// </summary>
    public int Count => _fields.Count;

    /// <summary>
    /// Gets the field names.
    /// </summary>
    public IEnumerable<string> FieldNames => _fields.Keys;

    /// <summary>
    /// Gets the value of the <c>method</c> field, which names what a message is. A reply
    /// carries no method, only the sequence number of the request it answers.
    /// </summary>
    public string Method => GetString("method") ?? string.Empty;

    /// <summary>
    /// Creates a message with a method already set.
    /// </summary>
    /// <param name="method">The HTSP method name.</param>
    /// <returns>The new message.</returns>
    public static HtspMessage Create(string method)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        var message = new HtspMessage();
        message.Set("method", method);
        return message;
    }

    /// <summary>
    /// Wraps an already decoded field dictionary.
    /// </summary>
    /// <param name="fields">The decoded fields. Ownership passes to the message.</param>
    /// <returns>The message.</returns>
    internal static HtspMessage FromDecoded(Dictionary<string, object> fields) => new(fields);

    /// <summary>
    /// Reports whether a field is present.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>Whether the field exists.</returns>
    public bool Contains(string name) => _fields.ContainsKey(name);

    /// <summary>
    /// Sets a string field.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <returns>This message, so calls can be chained.</returns>
    public HtspMessage Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        _fields[name] = value;
        return this;
    }

    /// <summary>
    /// Sets an integer field. HTSP carries every integer as a signed 64-bit value.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <returns>This message, so calls can be chained.</returns>
    public HtspMessage Set(string name, long value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        _fields[name] = value;
        return this;
    }

    /// <summary>
    /// Sets a binary field.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <returns>This message, so calls can be chained.</returns>
    public HtspMessage Set(string name, byte[] value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        _fields[name] = value;
        return this;
    }

    /// <summary>
    /// Sets a nested map field.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <returns>This message, so calls can be chained.</returns>
    public HtspMessage Set(string name, HtspMessage value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        _fields[name] = value;
        return this;
    }

    /// <summary>
    /// Sets a list field of integers, which is the only kind of list this client sends in
    /// practice: <c>subscriptionFilterStream</c> names the stream indices to disable that way.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <param name="values">The values.</param>
    /// <returns>This message, so calls can be chained.</returns>
    public HtspMessage Set(string name, IEnumerable<long> values)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(values);

        _fields[name] = values.Select(value => (object)value).ToList();
        return this;
    }

    /// <summary>
    /// Sets a list field of maps, which is the shape the server sends <c>streams</c> and
    /// <c>events</c> in.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <param name="values">The values.</param>
    /// <returns>This message, so calls can be chained.</returns>
    public HtspMessage Set(string name, IEnumerable<HtspMessage> values)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(values);

        _fields[name] = values.Select(value => (object)value).ToList();
        return this;
    }

    /// <summary>
    /// Removes a field.
    /// </summary>
    /// <param name="name">The field name.</param>
    public void Remove(string name) => _fields.Remove(name);

    /// <summary>
    /// Gets a string field.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or <see langword="null"/> when the field is absent or not a string.</returns>
    public string? GetString(string name)
        => _fields.TryGetValue(name, out var value) ? value as string : null;

    /// <summary>
    /// Gets an integer field.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or <see langword="null"/> when the field is absent or not an integer.</returns>
    public long? GetInt64(string name)
        => _fields.TryGetValue(name, out var value) && value is long number ? number : null;

    /// <summary>
    /// Gets an integer field narrowed to 32 bits.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or <see langword="null"/> when absent, not an integer, or out of range.</returns>
    public int? GetInt32(string name)
    {
        var value = GetInt64(name);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    /// <summary>
    /// Gets a field that is present as a non-zero integer, the form HTSP uses for flags.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>Whether the flag is set.</returns>
    public bool GetBoolean(string name) => GetInt64(name) is not null and not 0;

    /// <summary>
    /// Gets a binary field.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or <see langword="null"/> when the field is absent or not binary.</returns>
    public byte[]? GetBinary(string name)
        => _fields.TryGetValue(name, out var value) ? value as byte[] : null;

    /// <summary>
    /// Gets a nested map field.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or <see langword="null"/> when the field is absent or not a map.</returns>
    public HtspMessage? GetMap(string name)
        => _fields.TryGetValue(name, out var value) ? value as HtspMessage : null;

    /// <summary>
    /// Gets a list field as the maps it contains, skipping any element that is not a map.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The maps, empty when the field is absent or not a list.</returns>
    public IReadOnlyList<HtspMessage> GetMapList(string name)
    {
        if (!_fields.TryGetValue(name, out var value) || value is not IReadOnlyList<object> list)
        {
            return [];
        }

        return [.. list.OfType<HtspMessage>()];
    }

    /// <summary>
    /// Gets a list field as the integers it contains, skipping any element that is not one.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The integers, empty when the field is absent or not a list.</returns>
    public IReadOnlyList<long> GetInt64List(string name)
    {
        if (!_fields.TryGetValue(name, out var value) || value is not IReadOnlyList<object> list)
        {
            return [];
        }

        return [.. list.OfType<long>()];
    }

    /// <summary>
    /// Enumerates the fields, for the encoder.
    /// </summary>
    /// <returns>The fields.</returns>
    internal IEnumerable<KeyValuePair<string, object>> EnumerateFields() => _fields;

    /// <summary>
    /// Renders the message for a log line, without unfolding binary payloads.
    /// </summary>
    /// <returns>A single-line description.</returns>
    public override string ToString()
    {
        var body = string.Join(
            ", ",
            _fields
                .Where(field => !string.Equals(field.Key, "method", StringComparison.Ordinal))
                .Select(Describe));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(_fields.ContainsKey("method") ? Method : "<reply>")} {{{body}}}");
    }

    private static string Describe(KeyValuePair<string, object> field)
        => field.Value switch
        {
            byte[] binary => string.Create(CultureInfo.InvariantCulture, $"{field.Key}=<{binary.Length} bytes>"),
            HtspMessage map => string.Create(CultureInfo.InvariantCulture, $"{field.Key}={map}"),
            IReadOnlyList<object> list => string.Create(CultureInfo.InvariantCulture, $"{field.Key}=[{list.Count}]"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{field.Key}={field.Value}"),
        };
}
