#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareX.HelpersLib.Json
{
    /// <summary>
    /// Reads strings like Newtonsoft.Json did: numbers and booleans are accepted and kept as text.
    /// Web APIs often return ids as numbers while ShareX models them as strings.
    /// </summary>
    public sealed class LenientStringConverter : JsonConverter<string>
    {
        public override bool HandleNull => false;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    return GetRawText(ref reader);
                case JsonTokenType.True:
                    return bool.TrueString;
                case JsonTokenType.False:
                    return bool.FalseString;
                case JsonTokenType.Null:
                    return null;
                default:
                    throw new JsonException($"Unexpected token {reader.TokenType} when reading a string.");
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetString() ?? string.Empty;
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value);
        }

        private static string GetRawText(ref Utf8JsonReader reader)
        {
            return reader.HasValueSequence ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray()) : Encoding.UTF8.GetString(reader.ValueSpan);
        }
    }

    /// <summary>Reads booleans like Newtonsoft.Json did: "true"/"false" strings and numbers are accepted.</summary>
    public sealed class LenientBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.Number:
                    return reader.TryGetDouble(out double number) && number != 0;
                case JsonTokenType.String:
                    string? text = reader.GetString();
                    if (bool.TryParse(text, out bool value)) return value;
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed != 0;
                    throw new JsonException($"'{text}' is not a valid boolean.");
                default:
                    throw new JsonException($"Unexpected token {reader.TokenType} when reading a boolean.");
            }
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }

        public override bool ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return bool.Parse(reader.GetString() ?? bool.FalseString);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value ? bool.TrueString : bool.FalseString);
        }
    }

    /// <summary>
    /// Settings dates, matching Newtonsoft.Json DateTimeZoneHandling: written as UTC (Utc on save) and converted to local
    /// time when read (Local on load).
    /// </summary>
    public sealed class SettingsDateTimeConverter : JsonConverter<DateTime>
    {
        private const string IsoFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK";

        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            DateTime value;

            if (reader.TokenType == JsonTokenType.String)
            {
                string? text = reader.GetString();

                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
                {
                    throw new JsonException($"'{text}' is not a valid date.");
                }
            }
            else
            {
                value = reader.GetDateTime();
            }

            return value.Kind switch
            {
                DateTimeKind.Utc => value.ToLocalTime(),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Local),
                _ => value
            };
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            value = value.Kind switch
            {
                DateTimeKind.Local => value.ToUniversalTime(),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                _ => value
            };

            writer.WriteStringValue(value.ToString(IsoFormat, CultureInfo.InvariantCulture));
        }
    }
}
