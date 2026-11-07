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
/// Reads and writes PBLite, the "protobuf as a JSON array" format of the Google Messages endpoints.
/// </summary>
/// <remarks>
/// A message becomes an array with 1 slot for each field number. Slot 0 is field 1, and <c>null</c> is an unset field.
/// A nested message is a nested array, bytes are base64, and enums and integers are plain numbers. A field with
/// <c>pblite_binary</c> holds the binary protobuf or the UTF-8 string bytes in base64. The writer gives byte-for-byte
/// the output of the Go <c>encoding/json</c>, with its string escapes and number format. Thus the requests are the same
/// as from the original library. The reader is as lenient as the Go reader. An integer can be a JSON string, and a bool
/// can be a number. A trailing JSON object replaces slots by field number. Short arrays and extra slots are valid.
/// There is 1 intentional difference. Go decodes JSON numbers through float64, so it loses precision above 2^53. This
/// reader parses whole numbers from their exact text, so big int64 and uint64 values decode exactly. The code gets to
/// fields through <see cref="FieldDescriptor.Accessor"/> and never through reflection on type names. Thus it is safe
/// for Native AOT. Ported from
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
    /// The array length is the highest field number that the message type declares, also when the last slots are
    /// empty. Go does the same, and in practice the server cares about it. A field is set when it has presence and a
    /// value. A field without presence is set when it is not the default. A repeated field is set when it is not empty.
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
    /// The method is as lenient as Go. It skips null slots and ignores slots after the last known field. A trailing
    /// object such as <c>{"4":11}</c> has priority over the array slot with that field number.
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

    private static void WriteMessage(StringBuilder builder, IMessage message)
    {
        // The slots go up to the highest declared field number.
        IList<FieldDescriptor> fields = message.Descriptor.Fields.InFieldNumberOrder();
        int maxFieldNumber            = fields.Count == 0 ? 0 : fields[^1].FieldNumber;
        int nextField                 = 0;

        builder.Append('[');
        for (int slot = 1; slot <= maxFieldNumber; slot++)
        {
            if (slot > 1)
            {
                builder.Append(',');
            }

            if (fields[nextField].FieldNumber != slot)
            {
                builder.Append("null");
                continue;
            }

            FieldDescriptor field = fields[nextField++];
            if (!IsSet(field, message))
            {
                builder.Append("null");
                continue;
            }

            WriteField(builder, field, field.Accessor.GetValue(message));
        }

        builder.Append(']');
    }

    // The method tells whether a field gets a value or null, with the Go meaning of "set". It throws on a map field.
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

        // Without presence, a field is set when it is not the default. As in Go, negative zero is set.
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

    // The text must be plain ASCII, such as base64, because the method does not escape it.
    private static void WriteQuoted(StringBuilder builder, string text) => builder.Append('"').Append(text).Append('"');

    // The method writes a JSON string exactly as the Go encoding/json does. Go escapes < > & and U+2028/U+2029 as
    // lowercase \uXXXX. It writes short escapes for the quote, backslash, newline, carriage return, tab, backspace and
    // form feed. It escapes the other control characters. It replaces invalid UTF-16, a lone surrogate, with U+FFFD.
    // The default Utf8JsonWriter is different on each of these points. Thus this code writes the string by hand.
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

    // The method formats a double as the Go json.Marshal does. Go uses the shortest digits that round-trip. It uses
    // plain notation from 1e-6 up to 1e21, and exponent notation out of that range. A negative exponent has no zero
    // padding: 1e-7. A positive exponent has at least 2 digits: 1e+21. The .NET round-trip text gives the shortest
    // digits, and this method changes their layout.
    // As with the Go reflection, a float becomes a double first. NaN or infinity throws a PbLiteException.
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

        // The digits and the decimal exponent come from the round-trip text: magnitude = 0.DIGITS x 10^decimalExponent.
        double magnitude    = Math.Abs(value);
        string text         = magnitude.ToString("R", CultureInfo.InvariantCulture);
        int exponentAt      = text.IndexOf('E', StringComparison.Ordinal);
        int exponent        = exponentAt < 0
            ? 0
            : int.Parse(text.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string mantissa     = exponentAt < 0 ? text : text[..exponentAt];
        int pointAt         = mantissa.IndexOf('.', StringComparison.Ordinal);
        string allDigits    = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        int leadingZeros    = allDigits.Length - allDigits.TrimStart('0').Length;
        string digits       = allDigits.Trim('0');
        int decimalExponent = (pointAt < 0 ? mantissa.Length : pointAt) + exponent - leadingZeros;

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

            int scientific = decimalExponent - 1;
            result.Append('e').Append(scientific < 0 ? '-' : '+');
            result.Append(Math.Abs(scientific).ToString(scientific < 0 ? "0" : "00", CultureInfo.InvariantCulture));
        }
        else if (decimalExponent <= 0)
        {
            result.Append("0.").Append('0', -decimalExponent).Append(digits);
        }
        else if (decimalExponent >= digits.Length)
        {
            result.Append(digits).Append('0', decimalExponent - digits.Length);
        }
        else
        {
            result.Append(digits, 0, decimalExponent).Append('.').Append(digits, decimalExponent, digits.Length - decimalExponent);
        }

        return result.ToString();
    }

    // =========================================================================
    // READING
    // =========================================================================

    private static void ReadMessage(JsonElement array, IMessage message)
    {
        var slots = new List<JsonElement>(array.GetArrayLength());
        foreach (JsonElement slot in array.EnumerateArray())
        {
            slots.Add(slot);
        }

        // A trailing object holds replacement values by field number.
        JsonElement overrides = default;
        bool hasOverrides     = slots.Count > 0 && slots[^1].ValueKind == JsonValueKind.Object;
        if (hasOverrides)
        {
            overrides = slots[^1];
            slots.RemoveAt(slots.Count - 1);
        }

        foreach (FieldDescriptor field in message.Descriptor.Fields.InDeclarationOrder())
        {
            int index = field.FieldNumber - 1;

            // The override comes first, then the array slot. Null means unset.
            JsonElement value = default;
            bool found        = hasOverrides
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

    // The value is never null. The index is the zero-based slot index for the error messages.
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
        int itemIndex  = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            list.Add(ReadOne(item, itemIndex++, field));
        }
    }

    // The method returns a boxed value of the exact type that the field accessor expects.
    private static object ReadOne(JsonElement value, int index, FieldDescriptor field)
    {
        switch (field.FieldType)
        {
            case FieldType.Message:
                return ReadMessageValue(value, index, field);

            case FieldType.Bytes:
                return ByteString.CopyFrom(ReadBase64(value, index, field));

            case FieldType.Enum:
                return ReadNumber<int>(value, index, field, NumberStyles.None, allowString: false);

            case FieldType.Int32:
                return ReadNumber<int>(value, index, field, NumberStyles.AllowLeadingSign, allowString: true);

            case FieldType.Int64:
                return ReadNumber<long>(value, index, field, NumberStyles.AllowLeadingSign, allowString: true);

            case FieldType.UInt32:
                return ReadNumber<uint>(value, index, field, NumberStyles.None, allowString: true);

            case FieldType.UInt64:
                return ReadNumber<ulong>(value, index, field, NumberStyles.None, allowString: true);

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
                    : ReadText(value, index, field);

            case FieldType.Bool:
                return value.ValueKind switch
                {
                    JsonValueKind.True   => true,
                    JsonValueKind.False  => false,
                    JsonValueKind.Number => ReadDouble(value, index, field) != 0,
                    _                    => throw Fail(field, index, "bool or float", value),
                };

            default:
                throw new PbLiteException($"unsupported field type {field.FieldType} in {field.FullName}");
        }
    }

    // A nested message is a nested array. A pblite_binary message is base64 binary protobuf.
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

        IMessage nested = parser.ParseFrom([]);
        ReadMessage(value, nested);
        return nested;
    }

    private static byte[] ReadBase64(JsonElement value, int index, FieldDescriptor field)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Fail(field, index, "string", value);
        }

        try
        {
            return Convert.FromBase64String(ReadText(value, index, field));
        }
        catch (FormatException ex)
        {
            throw new PbLiteException($"failed to decode base64 at index {index} for field {field.FullName}", ex);
        }
    }

    // The method reads an integer from a JSON number or, when allowString is true, from a decimal string. As in Go,
    // an enum does not accept a string. The method parses a whole number from its exact text, so int64 and uint64
    // values above 2^53 stay exact. Other text, such as 1e3, goes through double and truncates. The Go float64 path
    // does the same.
    private static object ReadNumber<T>(JsonElement value, int index, FieldDescriptor field, NumberStyles styles, bool allowString)
        where T : IBinaryInteger<T>
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String when allowString:
                if (T.TryParse(ReadText(value, index, field), styles, CultureInfo.InvariantCulture, out T? parsed))
                {
                    return parsed;
                }

                throw new PbLiteException($"failed to parse {field.FieldType} at index {index} for field {field.FullName}");

            case JsonValueKind.Number:
                if (T.TryParse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out T? exact))
                {
                    return exact;
                }

                return T.CreateTruncating(ReadDouble(value, index, field));

            default:
                throw Fail(field, index, allowString ? "float64 or string" : "float64", value);
        }
    }

    private static double ReadDouble(JsonElement value, int index, FieldDescriptor field)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw Fail(field, index, "float64", value);
        }

        // A number out of range, such as 1e400, is also an error in the Go json.Unmarshal.
        if (!value.TryGetDouble(out double number) || !double.IsFinite(number))
        {
            throw new PbLiteException($"number out of range at index {index} for field {field.FullName}");
        }

        return number;
    }

    // JsonElement.GetString throws InvalidOperationException on an escape that is not valid UTF-16, such as the lone
    // surrogate "\ud800". The method reports that as a field error.
    private static string ReadText(JsonElement value, int index, FieldDescriptor field)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Fail(field, index, "string", value);
        }

        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException ex)
        {
            throw new PbLiteException($"invalid string at index {index} for field {field.FullName}", ex);
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static bool IsPbliteBinary(FieldDescriptor field)
    {
        FieldOptions? options = field.GetOptions();
        return options is not null && options.GetExtension(PbliteExtensions.PbliteBinary);
    }

    // The error text "expected X at index N for field F, got K" uses the same words as Go.
    private static PbLiteException Fail(FieldDescriptor field, int index, string expected, JsonElement actual)
        => new($"expected {expected} at index {index} for field {field.FullName}, got {actual.ValueKind}");
}
