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

using ShareX.HelpersLib.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ShareX.ImageEffectsLib
{
    /// <summary>
    /// Polymorphic <see cref="ImageEffect"/> (de)serialization compatible with the format previously written with TypeNameHandling.Auto:
    /// a "$type" property written first, either "Namespace.Type, Assembly" (settings) or the short type name (exported presets).
    /// Reading accepts both forms; an unknown type returns null.
    /// </summary>
    public sealed class ImageEffectJsonConverter : JsonConverter<ImageEffect>
    {
        private const string TypePropertyName = "$type";

        private static readonly Dictionary<string, Type> effectTypes = ImageEffectCatalog.All
            .Select(definition => definition.EffectType)
            .ToDictionary(type => type.Name, StringComparer.Ordinal);

        private readonly bool shortTypeNames;

        public ImageEffectJsonConverter() : this(false)
        {
        }

        public ImageEffectJsonConverter(bool shortTypeNames)
        {
            this.shortTypeNames = shortTypeNames;
        }

        /// <summary>Indented settings options writing short "$type" names, used to export and import image effect presets.</summary>
        public static JsonSerializerOptions PresetExportOptions { get; } = ShareXJson.Create(indented: true, settings: true,
            configure: options => options.Converters.Add(new ImageEffectJsonConverter(shortTypeNames: true)));

        public override ImageEffect? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            JsonNode? node = JsonNode.Parse(ref reader, new JsonNodeOptions { PropertyNameCaseInsensitive = options.PropertyNameCaseInsensitive });

            if (node == null)
            {
                return null;
            }

            if (node is not JsonObject obj)
            {
                throw new JsonException($"Expected a JSON object for {nameof(ImageEffect)}.");
            }

            string? typeName = null;

            if (obj.TryGetPropertyValue(TypePropertyName, out JsonNode? typeNode) && typeNode is JsonValue typeValue &&
                typeValue.TryGetValue(out string? text))
            {
                typeName = text;
            }

            Type? type = ResolveType(typeName);

            if (type == null)
            {
                return null;
            }

            obj.Remove(TypePropertyName);

            return (ImageEffect?)JsonSerializer.Deserialize(obj, options.GetTypeInfo(type));
        }

        public override void Write(Utf8JsonWriter writer, ImageEffect value, JsonSerializerOptions options)
        {
            Type type = value.GetType();
            JsonTypeInfo typeInfo = options.GetTypeInfo(type);
            JsonNode? node = JsonSerializer.SerializeToNode(value, typeInfo);

            writer.WriteStartObject();
            writer.WriteString(TypePropertyName, shortTypeNames ? type.Name : $"{type.FullName}, {type.Assembly.GetName().Name}");

            if (node is JsonObject obj)
            {
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    writer.WritePropertyName(property.Key);

                    if (property.Value == null)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        property.Value.WriteTo(writer, options);
                    }
                }
            }

            writer.WriteEndObject();
        }

        /// <summary>Accepts "Namespace.Type, Assembly", "Namespace.Type" or "Type".</summary>
        private static Type? ResolveType(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            string name = typeName!;
            int comma = name.IndexOf(',');

            if (comma >= 0)
            {
                name = name.Substring(0, comma);
            }

            name = name.Trim();
            int dot = name.LastIndexOfAny(['.', '+']);

            if (dot >= 0)
            {
                name = name.Substring(dot + 1);
            }

            return effectTypes.TryGetValue(name, out Type? type) ? type : null;
        }
    }
}
