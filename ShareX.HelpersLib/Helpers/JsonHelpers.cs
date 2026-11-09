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

﻿using ShareX.HelpersLib.Json;
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// File and string helpers on top of <see cref="ShareXJson"/>. <typeparamref name="T"/> must be declared in a source
    /// generated JsonSerializerContext registered with <see cref="ShareXJson.AddResolver"/>.
    /// Defaults to <see cref="ShareXJson.Settings"/>: indented, writable members only, enums by name.
    /// </summary>
    public static class JsonHelpers
    {
        public static void Serialize<T>(T obj, TextWriter textWriter, JsonSerializerOptions options = null)
        {
            if (textWriter != null)
            {
                textWriter.Write(SerializeToString(obj, options));
                textWriter.Flush();
            }
        }

        public static string SerializeToString<T>(T obj, JsonSerializerOptions options = null)
        {
            return ShareXJson.Serialize(obj, options ?? ShareXJson.Settings);
        }

        public static void SerializeToStream<T>(T obj, Stream stream, JsonSerializerOptions options = null)
        {
            if (stream != null)
            {
                ShareXJson.Serialize(stream, obj, options ?? ShareXJson.Settings);
            }
        }

        public static MemoryStream SerializeToMemoryStream<T>(T obj, JsonSerializerOptions options = null)
        {
            MemoryStream memoryStream = new MemoryStream();
            SerializeToStream(obj, memoryStream, options);
            memoryStream.Position = 0;
            return memoryStream;
        }

        public static void SerializeToFile<T>(T obj, string filePath, JsonSerializerOptions options = null)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                FileHelpers.CreateDirectoryFromFilePath(filePath);

                using (FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    SerializeToStream(obj, fileStream, options);
                }
            }
        }

        public static T Deserialize<T>(TextReader textReader, JsonSerializerOptions options = null)
        {
            if (textReader != null)
            {
                return DeserializeFromString<T>(textReader.ReadToEnd(), options);
            }

            return default;
        }

        /// <summary>Returns default when the JSON is empty or invalid (Newtonsoft.Json ignored errors here as well).</summary>
        public static T DeserializeFromString<T>(string json, JsonSerializerOptions options = null)
        {
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    return ShareXJson.Deserialize<T>(json, options ?? ShareXJson.Settings);
                }
                catch (JsonException e)
                {
                    DebugHelper.WriteException(e, $"{typeof(T).Name} JSON deserialization failed.");
                }
            }

            return default;
        }

        public static T DeserializeFromStream<T>(Stream stream, JsonSerializerOptions options = null)
        {
            if (stream != null)
            {
                using (StreamReader streamReader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return Deserialize<T>(streamReader, options);
                }
            }

            return default;
        }

        public static T DeserializeFromFile<T>(string filePath, JsonSerializerOptions options = null)
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fileStream.Length > 0)
                    {
                        return DeserializeFromStream<T>(fileStream, options);
                    }
                }
            }

            return default;
        }

        public static bool QuickVerifyJsonFile(string filePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (fileStream.Length > 1 && fileStream.ReadByte() == (byte)'{')
                        {
                            fileStream.Seek(-1, SeekOrigin.End);
                            return fileStream.ReadByte() == (byte)'}';
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
