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

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShareX.HelpersLib.Json
{
    /// <summary>Source generated System.Text.Json metadata for the types ShareX.HelpersLib serializes itself.</summary>
    [JsonSourceGenerationOptions(IncludeFields = true)]
    [JsonSerializable(typeof(GitHubUpdateChecker.GitHubRelease))]
    [JsonSerializable(typeof(List<GitHubUpdateChecker.GitHubRelease>))]
    [JsonSerializable(typeof(AppVeyorProject))]
    [JsonSerializable(typeof(AppVeyorProjectArtifact[]))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(string))]
    internal sealed partial class HelpersLibJsonContext : JsonSerializerContext
    {
        // Registered with ShareXJson by the ShareX.Roslyn generated ShareXGeneratedRegistration class.
    }
}
