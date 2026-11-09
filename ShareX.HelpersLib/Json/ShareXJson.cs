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
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ShareX.HelpersLib.Json
{
    /// <summary>
    /// Shared System.Text.Json configuration for ShareX.
    /// <para>
    /// Every ShareX assembly declares a source generated <see cref="JsonSerializerContext"/> for the types it serializes and
    /// registers it with <see cref="AddResolver"/> from a module initializer. All options below resolve type metadata through
    /// that registry, so serialization never falls back to reflection (trimming and NativeAOT safe).
    /// </para>
    /// <para>
    /// The options reproduce the Newtonsoft.Json behavior ShareX relied on, so existing settings and history files keep
    /// loading: public fields are included, property names are case-insensitive, numbers and booleans may be quoted,
    /// enums are written by name and unknown enum names are skipped, and System.Drawing types use their TypeConverter
    /// string format ("255, 0, 0", "10, 20").
    /// </para>
    /// </summary>
    public static class ShareXJson
    {
        private static readonly object resolversLock = new object();
        private static IJsonTypeInfoResolver[] resolvers = Array.Empty<IJsonTypeInfoResolver>();

        /// <summary>Registers a source generated context. ShareX.Roslyn generates the registration call for every JsonSerializerContext of a ShareX assembly.</summary>
        public static void AddResolver(IJsonTypeInfoResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));

            lock (resolversLock)
            {
                if (Array.IndexOf(resolvers, resolver) < 0)
                {
                    IJsonTypeInfoResolver[] updated = new IJsonTypeInfoResolver[resolvers.Length + 1];
                    resolvers.CopyTo(updated, 0);
                    updated[resolvers.Length] = resolver;
                    resolvers = updated;
                }
            }
        }

        private sealed class RegistryResolver : IJsonTypeInfoResolver
        {
            public static readonly RegistryResolver Instance = new RegistryResolver();

            public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
            {
                foreach (IJsonTypeInfoResolver resolver in resolvers)
                {
                    JsonTypeInfo? typeInfo = resolver.GetTypeInfo(type, options);

                    if (typeInfo != null)
                    {
                        return typeInfo;
                    }
                }

                return null;
            }
        }

        /// <summary>General purpose options (web API requests and responses, history, exports). Compact output.</summary>
        public static JsonSerializerOptions Default { get; } = Create(indented: false);

        /// <summary>Same as <see cref="Default"/> with indented output.</summary>
        public static JsonSerializerOptions Indented { get; } = Create(indented: true);

        /// <summary>Settings files: only writable members, [JsonEncrypt] values decrypted on read and written as plain text.</summary>
        public static JsonSerializerOptions Settings { get; } = Create(indented: true, settings: true);

        /// <summary>Settings files with [JsonEncrypt] values protected with DPAPI when written.</summary>
        public static JsonSerializerOptions SettingsEncrypted { get; } = Create(indented: true, settings: true, encrypt: true);

        /// <summary>Writable members only; nulls and default values ([DefaultValue] or CLR default) are omitted.</summary>
        public static JsonSerializerOptions IgnoreDefaults { get; } = Create(indented: true, settings: true, ignoreDefaults: true);

        public static JsonSerializerOptions Create(bool indented, bool settings = false, bool encrypt = false, bool ignoreDefaults = false,
            Action<JsonSerializerOptions>? configure = null)
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = indented,
                IndentSize = 2,
                IncludeFields = true,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                AllowOutOfOrderMetadataProperties = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = ignoreDefaults ? JsonIgnoreCondition.WhenWritingNull : JsonIgnoreCondition.Never,
                TypeInfoResolver = JsonTypeInfoResolver.WithAddedModifier(RegistryResolver.Instance,
                    typeInfo => JsonModifiers.Apply(typeInfo, settings, encrypt, ignoreDefaults))
            };

            // Settings (and files written with JsonHelpers) used StringEnumConverter; everything else wrote enums as numbers.
            options.Converters.Add(new LenientEnumConverterFactory(writeAsString: settings));
            options.Converters.Add(new LenientStringConverter());
            options.Converters.Add(new LenientBooleanConverter());
            DrawingJsonConverters.AddTo(options);
            options.Converters.Add(new TimeZoneInfoJsonConverter());

            if (settings)
            {
                // Newtonsoft.Json wrote settings dates in UTC and converted them back to local time when loading.
                options.Converters.Add(new SettingsDateTimeConverter());
            }

            configure?.Invoke(options);
            options.MakeReadOnly();
            return options;
        }

        /// <summary>Gets the source generated metadata for <typeparamref name="T"/>. Throws when no registered context declares it.</summary>
        public static JsonTypeInfo<T> GetTypeInfo<T>(JsonSerializerOptions? options = null)
        {
            return (JsonTypeInfo<T>)(options ?? Default).GetTypeInfo(typeof(T));
        }

        public static JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions? options = null)
        {
            return (options ?? Default).GetTypeInfo(type);
        }

        public static string Serialize<T>(T value, JsonSerializerOptions? options = null)
        {
            return JsonSerializer.Serialize(value, GetTypeInfo<T>(options));
        }

        public static void Serialize<T>(Stream stream, T value, JsonSerializerOptions? options = null)
        {
            JsonSerializer.Serialize(stream, value, GetTypeInfo<T>(options));
        }

        public static void Serialize<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions? options = null)
        {
            JsonSerializer.Serialize(writer, value, GetTypeInfo<T>(options));
        }

        public static JsonNode? SerializeToNode<T>(T value, JsonSerializerOptions? options = null)
        {
            return JsonSerializer.SerializeToNode(value, GetTypeInfo<T>(options));
        }

        public static T? Deserialize<T>(string? json, JsonSerializerOptions? options = null)
        {
            if (string.IsNullOrEmpty(json)) return default;
            return JsonSerializer.Deserialize(json, GetTypeInfo<T>(options));
        }

        public static T? Deserialize<T>(Stream stream, JsonSerializerOptions? options = null)
        {
            return JsonSerializer.Deserialize(stream, GetTypeInfo<T>(options));
        }

        public static T? Deserialize<T>(JsonNode? node, JsonSerializerOptions? options = null)
        {
            if (node == null) return default;
            return node.Deserialize(GetTypeInfo<T>(options));
        }

        public static T? Deserialize<T>(JsonElement element, JsonSerializerOptions? options = null)
        {
            return element.Deserialize(GetTypeInfo<T>(options));
        }

        public static JsonDocumentOptions DocumentOptions => new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>Parses JSON text (replacement for JToken.Parse / JObject.Parse).</summary>
        public static JsonNode? Parse(string json)
        {
            return JsonNode.Parse(json, documentOptions: DocumentOptions);
        }

        /// <summary>Parses JSON text, returning null for empty or invalid input.</summary>
        public static JsonNode? TryParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                return Parse(json!);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Reformats JSON text.</summary>
        public static string Format(string json, bool indented)
        {
            JsonNode? node = Parse(json);
            return node?.ToJsonString(indented ? Indented : Default) ?? "null";
        }

        /// <summary>Quotes and escapes a string as a JSON string literal (replacement for JsonConvert.ToString).</summary>
        public static string ToJsonString(string? text)
        {
            if (text == null) return "null";

            using MemoryStream stream = new MemoryStream();

            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStringValue(text);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
