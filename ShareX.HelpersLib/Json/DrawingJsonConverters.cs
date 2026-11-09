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
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareX.HelpersLib.Json
{
    /// <summary>
    /// Newtonsoft.Json serialized types that have a TypeConverter as the converter's invariant string, for example
    /// Color as "Black" or "235, 235, 235" and Point as "10, 20". These converters keep that format.
    /// </summary>
    public static class DrawingJsonConverters
    {
        public static JsonConverter<Color> Color { get; } = new TypeConverterJsonConverter<Color>(new ColorConverter());
        public static JsonConverter<Point> Point { get; } = new TypeConverterJsonConverter<Point>(new PointConverter());
        public static JsonConverter<Size> Size { get; } = new TypeConverterJsonConverter<Size>(new SizeConverter());
        public static JsonConverter<SizeF> SizeF { get; } = new TypeConverterJsonConverter<SizeF>(new SizeFConverter());
        public static JsonConverter<Rectangle> Rectangle { get; } = new TypeConverterJsonConverter<Rectangle>(new RectangleConverter());
        public static JsonConverter<ImageFont> ImageFont { get; } = new TypeConverterJsonConverter<ImageFont>(new ImageFontConverter());
        public static JsonConverter<ImageMargins> ImageMargins { get; } = new TypeConverterJsonConverter<ImageMargins>(new ImageMarginsConverter());

        public static void AddTo(JsonSerializerOptions options)
        {
            options.Converters.Add(Color);
            options.Converters.Add(Point);
            options.Converters.Add(Size);
            options.Converters.Add(SizeF);
            options.Converters.Add(Rectangle);
            options.Converters.Add(ImageFont);
            options.Converters.Add(ImageMargins);
        }
    }

    public sealed class TypeConverterJsonConverter<T> : JsonConverter<T>
    {
        private readonly TypeConverter converter;

        public TypeConverterJsonConverter(TypeConverter converter)
        {
            this.converter = converter;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Expected a string for {typeof(T).Name}.");
            }

            string? text = reader.GetString();
            object? value = converter.ConvertFromInvariantString(text ?? string.Empty);
            return value is T result ? result : default!;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(converter.ConvertToInvariantString(value));
        }

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            object? value = converter.ConvertFromInvariantString(reader.GetString() ?? string.Empty);
            return value is T result ? result : default!;
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(converter.ConvertToInvariantString(value) ?? string.Empty);
        }
    }

    /// <summary>
    /// Reads and writes <see cref="TimeZoneInfo"/> with the member names of its ISerializable data, which is what
    /// Newtonsoft.Json produced. Known zones are resolved from their id; unknown ones are rebuilt from the stored offset.
    /// </summary>
    public sealed class TimeZoneInfoJsonConverter : JsonConverter<TimeZoneInfo>
    {
        public override TimeZoneInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;

            if (reader.TokenType == JsonTokenType.String)
            {
                return Find(reader.GetString(), null, null, null);
            }

            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;

            string? id = GetString(root, "Id");
            string? displayName = GetString(root, "DisplayName");
            string? standardName = GetString(root, "StandardName");
            TimeSpan? offset = null;

            if (TryGetProperty(root, "BaseUtcOffset", out JsonElement offsetElement) && offsetElement.ValueKind == JsonValueKind.String &&
                TimeSpan.TryParse(offsetElement.GetString(), CultureInfo.InvariantCulture, out TimeSpan parsedOffset))
            {
                offset = parsedOffset;
            }

            return Find(id, displayName, standardName, offset);
        }

        public override void Write(Utf8JsonWriter writer, TimeZoneInfo value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Id", value.Id);
            writer.WriteString("DisplayName", value.DisplayName);
            writer.WriteString("StandardName", value.StandardName);
            writer.WriteString("DaylightName", value.DaylightName);
            writer.WriteString("BaseUtcOffset", value.BaseUtcOffset.ToString("c", CultureInfo.InvariantCulture));
            TimeZoneInfo.AdjustmentRule[] rules = value.GetAdjustmentRules();

            if (rules.Length == 0)
            {
                // ISerializable data has no rules array for zones without daylight saving time (UTC).
                writer.WriteNull("AdjustmentRules");
                writer.WriteBoolean("SupportsDaylightSavingTime", value.SupportsDaylightSavingTime);
                writer.WriteEndObject();
                return;
            }

            writer.WriteStartArray("AdjustmentRules");

            foreach (TimeZoneInfo.AdjustmentRule rule in rules)
            {
                writer.WriteStartObject();
                writer.WriteString("DateStart", rule.DateStart.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture));
                writer.WriteString("DateEnd", rule.DateEnd.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture));
                writer.WriteString("DaylightDelta", rule.DaylightDelta.ToString("c", CultureInfo.InvariantCulture));
                WriteTransition(writer, "DaylightTransitionStart", rule.DaylightTransitionStart);
                WriteTransition(writer, "DaylightTransitionEnd", rule.DaylightTransitionEnd);
                writer.WriteString("BaseUtcOffsetDelta", rule.BaseUtcOffsetDelta.ToString("c", CultureInfo.InvariantCulture));
                writer.WriteBoolean("NoDaylightTransitions", false);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("SupportsDaylightSavingTime", value.SupportsDaylightSavingTime);
            writer.WriteEndObject();
        }

        private static void WriteTransition(Utf8JsonWriter writer, string name, TimeZoneInfo.TransitionTime transition)
        {
            writer.WriteStartObject(name);
            writer.WriteString("TimeOfDay", transition.TimeOfDay.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture));
            writer.WriteNumber("Month", transition.Month);
            writer.WriteNumber("Week", transition.Week);
            writer.WriteNumber("Day", transition.Day);
            writer.WriteNumber("DayOfWeek", (int)transition.DayOfWeek);
            writer.WriteBoolean("IsFixedDateRule", transition.IsFixedDateRule);
            writer.WriteEndObject();
        }

        private static TimeZoneInfo Find(string? id, string? displayName, string? standardName, TimeSpan? offset)
        {
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            if (offset != null && !string.IsNullOrEmpty(id))
            {
                return TimeZoneInfo.CreateCustomTimeZone(id, offset.Value, displayName ?? id, standardName ?? id);
            }

            return TimeZoneInfo.Utc;
        }

        private static string? GetString(JsonElement element, string name)
        {
            return TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
