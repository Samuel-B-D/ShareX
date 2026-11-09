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
using ShareX.HelpersLib.Json;
using System.Text;
using System.Text.Json.Nodes;
using Image = SkiaSharp.SKBitmap;

namespace ShareX.Tools
{
    public class GeminiProvider : IAIProvider
    {
        private readonly string apiKey;
        private readonly string model;

        public GeminiProvider(string apiKey, string model)
        {
            this.apiKey = apiKey;
            this.model = model;
        }

        public async Task<string> AnalyzeImage(Image image, string prompt, string reasoningEffort, string verbosity)
        {
            string base64Image = SkiaImageHelpers.ImageToBase64(image, ImageFileFormat.Png);
            return await AnalyzeImageInternal(base64Image, prompt, reasoningEffort, verbosity);
        }

        public async Task<string> AnalyzeImage(string imagePath, string prompt, string reasoningEffort, string verbosity)
        {
            string base64Image = SkiaImageHelpers.ImageFileToBase64(imagePath);
            return await AnalyzeImageInternal(base64Image, prompt, reasoningEffort, verbosity);
        }

        private async Task<string> AnalyzeImageInternal(string base64Image, string prompt, string reasoningEffort, string verbosity)
        {
            using (HttpClient client = new HttpClient())
            {
                // Built with JsonObject instead of serializing anonymous types through reflection (trimming/NativeAOT safe).
                JsonObject payload = new JsonObject
                {
                    ["contents"] = new JsonArray(
                        new JsonObject
                        {
                            ["parts"] = new JsonArray(
                                new JsonObject { ["text"] = prompt },
                                new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/png", ["data"] = base64Image } }
                            )
                        }
                    )
                };

                string jsonPayload = payload.ToJsonString(ShareXJson.Default);
                StringContent content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

                HttpResponseMessage response = await client.PostAsync(url, content);
                string responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    // JsonNode instead of dynamic: the C# runtime binder is not supported under NativeAOT.
                    JsonNode responseObject = ShareXJson.Parse(responseString);
                    return JsonPath.ToText(responseObject["candidates"][0]["content"]["parts"][0]["text"]);
                }
                else
                {
                    throw new Exception(string.Format(Localization.Strings.GeminiProvider_API_error, responseString));
                }
            }
        }
    }
}
