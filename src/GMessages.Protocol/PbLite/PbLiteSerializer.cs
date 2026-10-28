using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Proto;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace GMessages.Protocol.PbLite;

/// <summary>
/// Reads and writes PBLite, the "protobuf as a JSON array" format Google Messages endpoints use.
/// </summary>
/// <remarks>
/// A message becomes an array with one slot per field number (slot 0 is field 1), <c>null</c> for unset fields, nested
/// messages as nested arrays, bytes as base64, and enums and integers as plain numbers. Fields marked
/// <c>pblite_binary</c> are instead the binary protobuf (or the UTF-8 string bytes) in base64. The writer matches Go's
/// <c>encoding/json</c> output byte for byte, including its string escaping and number formatting, so requests look
/// identical to the original library's. The reader is as lenient as Go's: integers may be JSON strings, bools may be
/// numbers, a trailing JSON object overrides slots by field number, and short arrays or extra slots are fine.
/// Fields are reached through <see cref="FieldDescriptor.Accessor"/> and never through reflection on type names, which
/// keeps it Native AOT safe. Ported from
/// <see href="https://github.com/mautrix/go-util/tree/main/pblite">go-util pblite</see> (<c>serialize.go</c> and
/// <c>deserialize.go</c>).
/// </remarks>
/// <seealso href="https://github.com/mautrix/go-util/tree/main/pblite"/>
public static class PbLiteSerializer
{
    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Writes a message as PBLite JSON.
    /// </summary>
    /// <remarks>
    /// The array length is the highest field number the message type declares, even when later slots are empty,
    /// because Go does the same and the server has been seen to care. A field counts as set when it has presence and a
    /// value, or has no presence and differs from its default; repeated fields are set when non-empty.
    /// </remarks>
    /// <param name="message">The message to write. Must not contain map, group, fixed-width or zigzag fields.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="PbLiteException">A set field has a type PBLite does not support, or a float is NaN or infinite.</exception>
    public static byte[] Serialize(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var builder = new StringBuilder();
        WriteMessage(builder, message);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Reads PBLite JSON into a new message.
    /// </summary>
    /// <remarks>
    /// Mirrors Go's leniency: null slots are skipped, slots past the last known field are ignored, and a trailing
    /// object such as <c>{"4":11}</c> wins over the array slot with that field number.
    /// </remarks>
    /// <typeparam name="T">The message type to create.</typeparam>
    /// <param name="json">UTF-8 PBLite JSON whose root is an array.</param>
    /// <returns>The populated message.</returns>
    /// <exception cref="PbLiteException">The JSON is invalid, the root is not an array, or a slot has the wrong shape; the message names the field.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> json) where T : IMessage<T>, new()
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new PbLiteException("invalid JSON: " + ex.Message, ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new PbLiteException($"expected array in JSON, got {document.RootElement.ValueKind}");
            }

            var message = new T();
            ReadMessage(document.RootElement, message);
            return message;
        }
    }

    // =========================================================================
    // WRITING
    // =========================================================================

    /// <summary>
    /// Writes one message as a JSON array, one slot per field number.
    /// </summary>
    /// <param name="builder">The output.</param>
    /// <param name="message">The message to write.</param>
    /// <exception cref="PbLiteException">A set field cannot be written.</exception>
    private static void WriteMessage(StringBuilder builder, IMessage message)
    {
        // Slots Run To The Highest Declared Field Number
        IList<FieldDescriptor> fields = message.Descriptor.Fields.InFieldNumberOrder();
        int max_field_number          = fields.Count == 0 ? 0 : fields[^1].FieldNumber;
        int next_field                = 0;

        builder.Append('[');
        for (int slot = 1; slot <= max_field_number; slot++)
        {
            if (slot > 1)
            {
                builder.Append(',');
            }

            // Unset Slot
            if (fields[next_field].FieldNumber != slot)
            {
                builder.Append("null");
                continue;
            }

            FieldDescriptor field = fields[next_field++];
            if (!IsSet(field, message))
            {
                builder.Append("null");
                continue;
            }

            WriteField(builder, field, field.Accessor.GetValue(message));
        }

        builder.Append(']');
    }

    /// <summary>
    /// Says whether a field would be written, using Go's definition of "set".
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="message">The message holding it.</param>
    /// <returns>True when the slot gets a value instead of null.</returns>
    /// <exception cref="PbLiteException">The field is a map.</exception>
    private static bool IsSet(FieldDescriptor field, IMessage message)
    {
        if (field.IsMap)
        {
            throw new PbLiteException($"unsupported map field {field.FullName}");
        }

        if (field.IsRepeated)
        {
            return ((IList)field.Accessor.GetValue(message)).Count > 0;
        }

        if (field.HasPresence)
        {
            return field.Accessor.HasValue(message);
        }

        // No Presence: Set Means Different From The Default (Negative Zero Counts As Set, As In Go)
        return field.Accessor.GetValue(message) switch
        {
            string text    => text.Length > 0,
            ByteString raw => raw.Length > 0,
            bool flag      => flag,
            int number     => number != 0,
            uint number    => number != 0,
            long number    => number != 0,
            ulong number   => number != 0,
            float number   => BitConverter.SingleToInt32Bits(number) != 0,
            double number  => BitConverter.DoubleToInt64Bits(number) != 0,
            Enum value     => Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0,
            _              => true,
        };
    }

    /// <summary>
    /// Writes a field value, as an array when the field is repeated.
    /// </summary>
    /// <param name="builder">The output.</param>
    /// <param name="field">The field.</param>
    /// <param name="value">The boxed value from the field accessor.</param>
    /// <exception cref="PbLiteException">The value cannot be written.</exception>
    private static void WriteField(StringBuilder builder, FieldDescriptor field, object value)
    {
        if (!field.IsRepeated)
        {
            WriteOne(builder, field, value);
            return;
        }

        builder.Append('[');
        bool first = true;
        foreach (object? item in (IList)value)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteOne(builder, field, item!);
        }

        builder.Append(']');
    }

    /// <summary>
    /// Writes one scalar, string, bytes or message value.
    /// </summary>
    /// <param name="builder">The output.</param>
    /// <param name="field">The field the value belongs to.</param>
    /// <param name="value">The boxed value.</param>
    /// <exception cref="PbLiteException">The field type is unsupported or a float is not finite.</exception>
    private static void WriteOne(StringBuilder builder, FieldDescriptor field, object value)
    {
        switch (field.FieldType)
        {
            case FieldType.Message:
                if (IsPbliteBinary(field))
                {
                    WriteQuoted(builder, Convert.ToBase64String(((IMessage)value).ToByteArray()));
                }
                else
                {
                    WriteMessage(builder, (IMessage)value);
                }
                break;

            case FieldType.Bytes:
                WriteQuoted(builder, Convert.ToBase64String(((ByteString)value).Span));
                break;

            case FieldType.Int32:
            case FieldType.Int64:
            case FieldType.UInt32:
            case FieldType.UInt64:
                builder.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                break;

            case FieldType.Float:
            case FieldType.Double:
                builder.Append(FormatDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture), field));
                break;

            case FieldType.Enum:
                builder.Append(Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;

            case FieldType.Bool:
                builder.Append((bool)value ? "true" : "false");
                break;

            case FieldType.String:
                if (IsPbliteBinary(field))
                {
                    WriteQuoted(builder, Convert.ToBase64String(Encoding.UTF8.GetBytes((string)value)));
                }
                else
                {
                    WriteString(builder, (string)value);
                }
                break;

            default:
                throw new PbLiteException($"unsupported field type {field.FieldType} in {field.FullName}");
        }
    }

    /// <summary>
    /// Writes a string that needs no escaping (base64) between quotes.
    /// </summary>
    /// <param name="builder">The output.</param>
    /// <param name="text">The text, which must be plain ASCII.</param>
    private static void WriteQuoted(StringBuilder builder, string text) => builder.Append('"').Append(text).Append('"');

    /// <summary>
    /// Writes a JSON string exactly as Go's <c>encoding/json</c> does.
    /// </summary>
    /// <remarks>
    /// Go escapes <c>&lt; &gt; &amp;</c> and U+2028/U+2029 as lowercase <c>\uXXXX</c>, writes short escapes for
    /// quote, backslash, newline, carriage return, tab, backspace and form feed, escapes other control characters, and
    /// replaces invalid UTF-16 (lone surrogates) with U+FFFD. The default <see cref="Utf8JsonWriter"/> differs on every
    /// one of those, which is why this is written by hand.
    /// </remarks>
    /// <param name="builder">The output.</param>
    /// <param name="text">The string to write.</param>
    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"':  builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;

                case '<':
                case '>':
                case '&':
                case (char)0x2028:
                case (char)0x2029:
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;

                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        builder.Append(c).Append(text[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        builder.Append("\\ufffd");
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Formats a double the way Go's <c>json.Marshal</c> does.
    /// </summary>
    /// <remarks>
    /// Go uses the shortest digits that round-trip, plain notation between 1e-6 and 1e21, and exponent notation
    /// outside that range with no zero padding on negative exponents (<c>1e-7</c>, <c>1e+21</c>). .NET's round-trip
    /// text supplies the shortest digits; this re-lays them out. Floats widen to double first, as Go's reflection does.
    /// </remarks>
    /// <param name="value">The number.</param>
    /// <param name="field">The field, for the error message.</param>
    /// <returns>The JSON number text.</returns>
    /// <exception cref="PbLiteException"><paramref name="value"/> is NaN or infinite.</exception>
    private static string FormatDouble(double value, FieldDescriptor field)
    {
        if (!double.IsFinite(value))
        {
            throw new PbLiteException($"unsupported value {value.ToString(CultureInfo.InvariantCulture)} in {field.FullName}");
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }

        // Pull Digits And Decimal Exponent Out Of The Round-Trip Text (magnitude = 0.DIGITS x 10^decimal_exponent)
        double magnitude = Math.Abs(value);
        string text      = magnitude.ToString("R", CultureInfo.InvariantCulture);
        int exponent_at  = text.IndexOf('E', StringComparison.Ordinal);
        int exponent     = exponent_at < 0
            ? 0
            : int.Parse(text.AsSpan(exponent_at + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string mantissa  = exponent_at < 0 ? text : text[..exponent_at];
        int point_at     = mantissa.IndexOf('.', StringComparison.Ordinal);
        string all_digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        int leading_zeros = all_digits.Length - all_digits.TrimStart('0').Length;
        string digits     = all_digits.Trim('0');
        int decimal_exponent = (point_at < 0 ? mantissa.Length : point_at) + exponent - leading_zeros;

        // Lay Out
        var result = new StringBuilder();
        if (value < 0)
        {
            result.Append('-');
        }

        if (magnitude < 1e-6 || magnitude >= 1e21)
        {
            result.Append(digits[0]);
            if (digits.Length > 1)
            {
                result.Append('.').Append(digits, 1, digits.Length - 1);
            }

            int scientific = decimal_exponent - 1;
            result.Append('e').Append(scientific < 0 ? '-' : '+');
            result.Append(Math.Abs(scientific).ToString(scientific < 0 ? "0" : "00", CultureInfo.InvariantCulture));
        }
        else if (decimal_exponent <= 0)
        {
            result.Append("0.").Append('0', -decimal_exponent).Append(digits);
        }
        else if (decimal_exponent >= digits.Length)
        {
            result.Append(digits).Append('0', decimal_exponent - digits.Length);
        }
        else
        {
            result.Append(digits, 0, decimal_exponent).Append('.').Append(digits, decimal_exponent, digits.Length - decimal_exponent);
        }

        return result.ToString();
    }

    // =========================================================================
    // READING
    // =========================================================================

    /// <summary>
    /// Fills a message from a PBLite array.
    /// </summary>
    /// <param name="array">The JSON array.</param>
    /// <param name="message">The empty message to fill.</param>
    /// <exception cref="PbLiteException">A slot has the wrong shape.</exception>
    private static void ReadMessage(JsonElement array, IMessage message)
    {
        var slots = new List<JsonElement>(array.GetArrayLength());
        foreach (JsonElement slot in array.EnumerateArray())
        {
            slots.Add(slot);
        }

        // A Trailing Object Holds Overrides Keyed By Field Number
        JsonElement overrides = default;
        bool has_overrides    = slots.Count > 0 && slots[^1].ValueKind == JsonValueKind.Object;
        if (has_overrides)
        {
            overrides = slots[^1];
            slots.RemoveAt(slots.Count - 1);
        }

        foreach (FieldDescriptor field in message.Descriptor.Fields.InDeclarationOrder())
        {
            int index = field.FieldNumber - 1;

            // Override First, Then The Array Slot; Null Means Unset
            JsonElement value = default;
            bool found        = has_overrides
                && overrides.TryGetProperty((index + 1).ToString(CultureInfo.InvariantCulture), out value)
                && value.ValueKind != JsonValueKind.Null;
            if (!found && index < slots.Count)
            {
                value = slots[index];
                found = value.ValueKind != JsonValueKind.Null;
            }

            if (!found)
            {
                continue;
            }

            ReadField(message, field, value, index);
        }
    }

    /// <summary>
    /// Reads one slot into its field.
    /// </summary>
    /// <param name="message">The message being filled.</param>
    /// <param name="field">The field.</param>
    /// <param name="value">The slot value, never null.</param>
    /// <param name="index">The zero-based slot index, for errors.</param>
    /// <exception cref="PbLiteException">The slot has the wrong shape.</exception>
    private static void ReadField(IMessage message, FieldDescriptor field, JsonElement value, int index)
    {
        if (field.IsMap)
        {
            throw new PbLiteException($"unsupported map field {field.FullName}");
        }

        if (!field.IsRepeated)
        {
            field.Accessor.SetValue(message, ReadOne(value, index, field));
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Fail(field, index, "untyped array for repeated field", value);
        }

        var list       = (IList)field.Accessor.GetValue(message);
        int item_index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            list.Add(ReadOne(item, item_index++, field));
        }
    }

    /// <summary>
    /// Converts one JSON value to the boxed .NET value of a field.
    /// </summary>
    /// <param name="value">The JSON value.</param>
    /// <param name="index">The slot or list index, for errors.</param>
    /// <param name="field">The field.</param>
    /// <returns>A boxed value of the exact type the field accessor expects.</returns>
    /// <exception cref="PbLiteException">The value has the wrong shape.</exception>
    private static object ReadOne(JsonElement value, int index, FieldDescriptor field)
    {
        switch (field.FieldType)
        {
            case FieldType.Message:
                return ReadMessageValue(value, index, field);

            case FieldType.Bytes:
                return ByteString.CopyFrom(ReadBase64(value, index, field));

            case FieldType.Enum:
                return ReadNumber<int>(value, index, field, NumberStyles.None, allow_string: false);

            case FieldType.Int32:
                return ReadNumber<int>(value, index, field, NumberStyles.AllowLeadingSign, allow_string: true);

            case FieldType.Int64:
                return ReadNumber<long>(value, index, field, NumberStyles.AllowLeadingSign, allow_string: true);

            case FieldType.UInt32:
                return ReadNumber<uint>(value, index, field, NumberStyles.None, allow_string: true);

            case FieldType.UInt64:
                return ReadNumber<ulong>(value, index, field, NumberStyles.None, allow_string: true);

            case FieldType.Float:
                return (float)ReadDouble(value, index, field);

            case FieldType.Double:
                return ReadDouble(value, index, field);

            case FieldType.String:
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw Fail(field, index, "string", value);
                }

                return IsPbliteBinary(field)
                    ? Encoding.UTF8.GetString(ReadBase64(value, index, field))
                    : value.GetString()!;

            case FieldType.Bool:
                return value.ValueKind switch
                {
                    JsonValueKind.True   => true,
                    JsonValueKind.False  => false,
                    JsonValueKind.Number => value.GetDouble() != 0,
                    _                    => throw Fail(field, index, "bool or float", value),
                };

            default:
                throw new PbLiteException($"unsupported field type {field.FieldType} in {field.FullName}");
        }
    }

    /// <summary>
    /// Reads a nested message, from a nested array or (for <c>pblite_binary</c>) base64 binary protobuf.
    /// </summary>
    /// <param name="value">The JSON value.</param>
    /// <param name="index">The slot or list index, for errors.</param>
    /// <param name="field">The message-typed field.</param>
    /// <returns>The new message.</returns>
    /// <exception cref="PbLiteException">The value has the wrong shape or the binary protobuf is malformed.</exception>
    private static IMessage ReadMessageValue(JsonElement value, int index, FieldDescriptor field)
    {
        MessageParser parser = field.MessageType.Parser;

        if (IsPbliteBinary(field))
        {
            byte[] bytes = ReadBase64(value, index, field);
            try
            {
                return parser.ParseFrom(bytes);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new PbLiteException($"failed to unmarshal binary protobuf at index {index} for field {field.FullName}", ex);
            }
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Fail(field, index, "untyped array", value);
        }

        IMessage nested = parser.ParseFrom(ReadOnlySpan<byte>.Empty);
        ReadMessage(value, nested);
        return nested;
    }

    /// <summary>
    /// Reads a base64 JSON string.
    /// </summary>
    /// <param name="value">The JSON value.</param>
    /// <param name="index">The slot or list index, for errors.</param>
    /// <param name="field">The field, for errors.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="PbLiteException">The value is not a string or not valid base64.</exception>
    private static byte[] ReadBase64(JsonElement value, int index, FieldDescriptor field)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Fail(field, index, "string", value);
        }

        try
        {
            return Convert.FromBase64String(value.GetString()!);
        }
        catch (FormatException ex)
        {
            throw new PbLiteException($"failed to decode base64 at index {index} for field {field.FullName}", ex);
        }
    }

    /// <summary>
    /// Reads an integer from a JSON number or, when allowed, a decimal string.
    /// </summary>
    /// <remarks>
    /// Whole numbers are parsed from their exact text so int64 and uint64 values above 2^53 survive; anything else
    /// (such as <c>1e3</c>) goes through double and truncates, as Go's float64 path does.
    /// </remarks>
    /// <typeparam name="T">The integer type of the field.</typeparam>
    /// <param name="value">The JSON value.</param>
    /// <param name="index">The slot or list index, for errors.</param>
    /// <param name="field">The field, for errors.</param>
    /// <param name="styles">Which signs the number text may carry.</param>
    /// <param name="allow_string">Whether a JSON string is accepted (not for enums, as in Go).</param>
    /// <returns>The number, boxed as <typeparamref name="T"/>.</returns>
    /// <exception cref="PbLiteException">The value is neither a number nor a parsable string.</exception>
    private static object ReadNumber<T>(JsonElement value, int index, FieldDescriptor field, NumberStyles styles, bool allow_string)
        where T : IBinaryInteger<T>
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String when allow_string:
                if (T.TryParse(value.GetString(), styles, CultureInfo.InvariantCulture, out T? parsed))
                {
                    return parsed;
                }

                throw new PbLiteException($"failed to parse {field.FieldType} at index {index} for field {field.FullName}");

            case JsonValueKind.Number:
                if (T.TryParse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out T? exact))
                {
                    return exact;
                }

                return T.CreateTruncating(value.GetDouble());

            default:
                throw Fail(field, index, allow_string ? "float64 or string" : "float64", value);
        }
    }

    /// <summary>
    /// Reads a floating-point number.
    /// </summary>
    /// <param name="value">The JSON value.</param>
    /// <param name="index">The slot or list index, for errors.</param>
    /// <param name="field">The field, for errors.</param>
    /// <returns>The number.</returns>
    /// <exception cref="PbLiteException">The value is not a JSON number.</exception>
    private static double ReadDouble(JsonElement value, int index, FieldDescriptor field)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw Fail(field, index, "float64", value);
        }

        return value.GetDouble();
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Reads the <c>pblite_binary</c> field option.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>True when the field is carried as base64 binary.</returns>
    private static bool IsPbliteBinary(FieldDescriptor field)
    {
        FieldOptions? options = field.GetOptions();
        return options is not null && options.GetExtension(PbliteExtensions.PbliteBinary);
    }

    /// <summary>
    /// Builds the "expected X at index N for field F, got K" error, worded like Go's.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="index">The slot or list index.</param>
    /// <param name="expected">What was expected.</param>
    /// <param name="actual">The value that was found.</param>
    /// <returns>The exception to throw.</returns>
    private static PbLiteException Fail(FieldDescriptor field, int index, string expected, JsonElement actual)
        => new($"expected {expected} at index {index} for field {field.FullName}, got {actual.ValueKind}");
}
