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
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareX.HelpersLib.Json
{
    /// <summary>
    /// Creates <see cref="LenientEnumConverter{T}"/> instances for enums registered with <see cref="Register{T}"/>.
    /// ShareX.Roslyn generates a module initializer per assembly that registers every enum of that assembly, because
    /// NativeAOT cannot instantiate JsonConverter&lt;TEnum&gt; for an arbitrary enum at runtime.
    /// </summary>
    public sealed class LenientEnumConverterFactory : JsonConverterFactory
    {
        private static readonly ConcurrentDictionary<Type, Func<bool, JsonConverter>> factories = new ConcurrentDictionary<Type, Func<bool, JsonConverter>>();

        private readonly bool writeAsString;

        /// <param name="writeAsString">Write enum values by name (ShareX settings) instead of by number (Newtonsoft.Json default).</param>
        public LenientEnumConverterFactory(bool writeAsString = true)
        {
            this.writeAsString = writeAsString;
        }

        public static void Register<T>() where T : struct, Enum
        {
            factories.TryAdd(typeof(T), static writeAsString => new LenientEnumConverter<T>(writeAsString));
        }

        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert.IsEnum && factories.ContainsKey(typeToConvert);
        }

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return factories.TryGetValue(typeToConvert, out Func<bool, JsonConverter>? factory) ? factory(writeAsString) : null;
        }

        [ThreadStatic]
        private static bool lastValueUnknown;

        internal static void ResetUnknown() => lastValueUnknown = false;

        internal static void MarkUnknown() => lastValueUnknown = true;

        /// <summary>Returns true (once) when the last enum value read on this thread did not match any member.</summary>
        internal static bool ConsumeUnknown()
        {
            bool unknown = lastValueUnknown;
            lastValueUnknown = false;
            return unknown;
        }
    }

    /// <summary>
    /// Reads enum names (case-insensitive, comma separated flags) or numbers. An unknown name does not fail the whole
    /// document like JsonStringEnumConverter does: the member keeps its current value, as with Newtonsoft.Json.
    /// </summary>
    public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly bool writeAsString;

        public LenientEnumConverter(bool writeAsString)
        {
            this.writeAsString = writeAsString;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Clear a flag left by an unknown value in a collection, which has no member setter to consume it.
            LenientEnumConverterFactory.ResetUnknown();

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return Parse(reader.GetString());
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out long number)) return (T)Enum.ToObject(typeof(T), number);
                    if (reader.TryGetUInt64(out ulong unsignedNumber)) return (T)Enum.ToObject(typeof(T), unsignedNumber);
                    break;
            }

            LenientEnumConverterFactory.MarkUnknown();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            string text = value.ToString();

            if (!writeAsString || IsNumeric(text))
            {
                // Undefined values (and every value when not writing names) are written as numbers, like Newtonsoft.Json.
                WriteNumber(writer, value);
            }
            else
            {
                writer.WriteStringValue(text);
            }
        }

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return Parse(reader.GetString());
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString());
        }

        private static T Parse(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse(text, ignoreCase: true, out T value))
            {
                return value;
            }

            LenientEnumConverterFactory.MarkUnknown();
            return default;
        }

        private static bool IsNumeric(string text)
        {
            return text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '-');
        }

        private static void WriteNumber(Utf8JsonWriter writer, T value)
        {
            switch (Type.GetTypeCode(Enum.GetUnderlyingType(typeof(T))))
            {
                case TypeCode.UInt64:
                    writer.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                    break;
                default:
                    writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
            }
        }
    }
}
