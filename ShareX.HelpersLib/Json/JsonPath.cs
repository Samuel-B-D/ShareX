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
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShareX.HelpersLib.Json
{
    /// <summary>
    /// Minimal JSONPath evaluator for <see cref="JsonNode"/>, covering what custom uploaders use with Newtonsoft.Json
    /// SelectToken: an optional root ($), member access (.name, ['name'], ["name"]), array indexes ([0], negative from
    /// the end) and wildcards ([*], .*), returning the first match.
    /// </summary>
    public static class JsonPath
    {
        public static JsonNode? SelectToken(JsonNode? root, string? path)
        {
            if (root == null) return null;
            if (string.IsNullOrWhiteSpace(path)) return root;

            List<JsonNode?> current = new List<JsonNode?> { root };

            foreach (Segment segment in Parse(path!))
            {
                List<JsonNode?> next = new List<JsonNode?>();

                foreach (JsonNode? node in current)
                {
                    switch (segment.Kind)
                    {
                        case SegmentKind.Name when node is JsonObject obj:
                            if (TryGetProperty(obj, segment.Name!, out JsonNode? child)) next.Add(child);
                            break;
                        case SegmentKind.Index when node is JsonArray array:
                            int index = segment.Index < 0 ? array.Count + segment.Index : segment.Index;
                            if (index >= 0 && index < array.Count) next.Add(array[index]);
                            break;
                        case SegmentKind.Wildcard when node is JsonArray array:
                            next.AddRange(array);
                            break;
                        case SegmentKind.Wildcard when node is JsonObject obj:
                            foreach (KeyValuePair<string, JsonNode?> pair in obj) next.Add(pair.Value);
                            break;
                    }
                }

                current = next;

                if (current.Count == 0)
                {
                    return null;
                }
            }

            return current[0];
        }

        /// <summary>String value of the selected token: strings as is, other values as their JSON text.</summary>
        public static string? SelectString(JsonNode? root, string? path)
        {
            return ToText(SelectToken(root, path));
        }

        public static string? ToText(JsonNode? node)
        {
            if (node == null) return null;

            if (node is JsonValue value)
            {
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        return value.GetValue<string>();
                    case JsonValueKind.True:
                        return bool.TrueString;
                    case JsonValueKind.False:
                        return bool.FalseString;
                    case JsonValueKind.Null:
                        return null;
                }
            }

            return node.ToJsonString();
        }

        private static bool TryGetProperty(JsonObject obj, string name, out JsonNode? value)
        {
            if (obj.TryGetPropertyValue(name, out value))
            {
                return true;
            }

            foreach (KeyValuePair<string, JsonNode?> pair in obj)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private enum SegmentKind
        {
            Name,
            Index,
            Wildcard
        }

        private readonly struct Segment
        {
            public SegmentKind Kind { get; }
            public string? Name { get; }
            public int Index { get; }

            public Segment(SegmentKind kind, string? name = null, int index = 0)
            {
                Kind = kind;
                Name = name;
                Index = index;
            }
        }

        private static IEnumerable<Segment> Parse(string path)
        {
            int i = 0;
            path = path.Trim();

            if (path.StartsWith("$", StringComparison.Ordinal))
            {
                i = 1;
            }

            while (i < path.Length)
            {
                char c = path[i];

                if (c == '.')
                {
                    i++;

                    if (i < path.Length && path[i] == '[')
                    {
                        // "$.[0]" and "a.['b']": the bracket segment follows directly.
                        continue;
                    }

                    if (i < path.Length && path[i] == '*')
                    {
                        i++;
                        yield return new Segment(SegmentKind.Wildcard);
                        continue;
                    }

                    yield return new Segment(SegmentKind.Name, ReadName(path, ref i));
                }
                else if (c == '[')
                {
                    i++;
                    int end = path.IndexOf(']', i);
                    if (end < 0) throw new FormatException($"Unterminated '[' in JSON path: {path}");
                    string content = path.Substring(i, end - i).Trim();
                    i = end + 1;

                    if (content == "*")
                    {
                        yield return new Segment(SegmentKind.Wildcard);
                    }
                    else if (content.Length >= 2 && (content[0] == '\'' || content[0] == '"') && content[content.Length - 1] == content[0])
                    {
                        yield return new Segment(SegmentKind.Name, content.Substring(1, content.Length - 2));
                    }
                    else if (int.TryParse(content, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    {
                        yield return new Segment(SegmentKind.Index, index: index);
                    }
                    else
                    {
                        yield return new Segment(SegmentKind.Name, content);
                    }
                }
                else
                {
                    yield return new Segment(SegmentKind.Name, ReadName(path, ref i));
                }
            }
        }

        private static string ReadName(string path, ref int i)
        {
            StringBuilder sb = new StringBuilder();

            while (i < path.Length && path[i] != '.' && path[i] != '[')
            {
                sb.Append(path[i]);
                i++;
            }

            return sb.ToString();
        }
    }
}
