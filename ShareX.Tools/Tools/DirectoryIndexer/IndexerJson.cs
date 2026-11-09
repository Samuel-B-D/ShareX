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

#nullable disable

using ShareX.HelpersLib;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShareX.Tools
{
    public class IndexerJson : Indexer
    {
        private Utf8JsonWriter jsonWriter;

        public IndexerJson(IndexerSettings indexerSettings) : base(indexerSettings)
        {
        }

        public override string Index(string folderPath)
        {
            FolderInfo folderInfo = GetFolderInfo(folderPath);
            folderInfo.Update();

            // Same output as the former JsonTextWriter with Formatting.Indented: two space indentation,
            // platform new lines and non-ASCII characters written as is.
            JsonWriterOptions writerOptions = new JsonWriterOptions
            {
                Indented = true,
                IndentSize = 2,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            using (MemoryStream stream = new MemoryStream())
            {
                using (jsonWriter = new Utf8JsonWriter(stream, writerOptions))
                {
                    jsonWriter.WriteStartObject();
                    IndexFolder(folderInfo);
                    jsonWriter.WriteEndObject();
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        protected override void IndexFolder(FolderInfo dir, int level = 0)
        {
            if (settings.CreateParseableJson)
            {
                IndexFolderParseable(dir);
            }
            else
            {
                IndexFolderSimple(dir);
            }
        }

        private void IndexFolderSimple(FolderInfo dir)
        {
            jsonWriter.WritePropertyName(dir.FolderName);
            jsonWriter.WriteStartArray();

            foreach (FolderInfo subdir in dir.Folders)
            {
                jsonWriter.WriteStartObject();
                IndexFolder(subdir);
                jsonWriter.WriteEndObject();
            }

            foreach (FileInfo fi in dir.Files)
            {
                jsonWriter.WriteStringValue(fi.Name);
            }

            jsonWriter.WriteEndArray();
        }

        private void IndexFolderParseable(FolderInfo dir)
        {
            jsonWriter.WritePropertyName("Name");
            jsonWriter.WriteStringValue(dir.FolderName);

            if (settings.ShowSizeInfo)
            {
                jsonWriter.WritePropertyName("Size");
                jsonWriter.WriteStringValue(dir.Size.ToSizeString(settings.BinaryUnits));
            }

            if (dir.Folders.Count > 0)
            {
                jsonWriter.WritePropertyName("Folders");
                jsonWriter.WriteStartArray();

                foreach (FolderInfo subdir in dir.Folders)
                {
                    jsonWriter.WriteStartObject();
                    IndexFolder(subdir);
                    jsonWriter.WriteEndObject();
                }

                jsonWriter.WriteEndArray();
            }

            if (dir.Files.Count > 0)
            {
                jsonWriter.WritePropertyName("Files");
                jsonWriter.WriteStartArray();

                foreach (FileInfo fi in dir.Files)
                {
                    jsonWriter.WriteStartObject();

                    jsonWriter.WritePropertyName("Name");
                    jsonWriter.WriteStringValue(fi.Name);

                    if (settings.ShowSizeInfo)
                    {
                        jsonWriter.WritePropertyName("Size");
                        jsonWriter.WriteStringValue(fi.Length.ToSizeString(settings.BinaryUnits));
                    }

                    jsonWriter.WriteEndObject();
                }

                jsonWriter.WriteEndArray();
            }
        }
    }
}
