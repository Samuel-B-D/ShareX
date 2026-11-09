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
using System.Text.Json.Serialization;

namespace ShareX.ImageEffectsLib
{
    /// <summary>Source generated System.Text.Json metadata for image effect presets and every concrete <see cref="ImageEffect"/>.</summary>
    [JsonSourceGenerationOptions(IncludeFields = true)]
    [JsonSerializable(typeof(ImageEffectPreset))]
    [JsonSerializable(typeof(List<ImageEffectPreset>))]
    [JsonSerializable(typeof(ImageEffect))]
    [JsonSerializable(typeof(Alpha))]
    [JsonSerializable(typeof(AutoCrop))]
    [JsonSerializable(typeof(BlackWhite))]
    [JsonSerializable(typeof(Blur))]
    [JsonSerializable(typeof(Brightness))]
    [JsonSerializable(typeof(Canvas))]
    [JsonSerializable(typeof(ColorDepth))]
    [JsonSerializable(typeof(Colorize))]
    [JsonSerializable(typeof(Contrast))]
    [JsonSerializable(typeof(Crop))]
    [JsonSerializable(typeof(DrawBackground))]
    [JsonSerializable(typeof(DrawBackgroundImage))]
    [JsonSerializable(typeof(DrawBorder))]
    [JsonSerializable(typeof(DrawCheckerboard))]
    [JsonSerializable(typeof(DrawImage))]
    [JsonSerializable(typeof(DrawParticles))]
    [JsonSerializable(typeof(DrawText))]
    [JsonSerializable(typeof(DrawTextEx))]
    [JsonSerializable(typeof(EdgeDetect))]
    [JsonSerializable(typeof(Emboss))]
    [JsonSerializable(typeof(Flip))]
    [JsonSerializable(typeof(ForceProportions))]
    [JsonSerializable(typeof(Gamma))]
    [JsonSerializable(typeof(GaussianBlur))]
    [JsonSerializable(typeof(Glow))]
    [JsonSerializable(typeof(Grayscale))]
    [JsonSerializable(typeof(Hue))]
    [JsonSerializable(typeof(Inverse))]
    [JsonSerializable(typeof(MatrixColor))]
    [JsonSerializable(typeof(MatrixConvolution))]
    [JsonSerializable(typeof(MeanRemoval))]
    [JsonSerializable(typeof(Outline))]
    [JsonSerializable(typeof(Pixelate))]
    [JsonSerializable(typeof(Polaroid))]
    [JsonSerializable(typeof(RGBSplit))]
    [JsonSerializable(typeof(Reflection))]
    [JsonSerializable(typeof(ReplaceColor))]
    [JsonSerializable(typeof(Resize))]
    [JsonSerializable(typeof(Rotate))]
    [JsonSerializable(typeof(RoundedCorners))]
    [JsonSerializable(typeof(Saturation))]
    [JsonSerializable(typeof(Scale))]
    [JsonSerializable(typeof(SelectiveColor))]
    [JsonSerializable(typeof(Sepia))]
    [JsonSerializable(typeof(Shadow))]
    [JsonSerializable(typeof(Sharpen))]
    [JsonSerializable(typeof(Skew))]
    [JsonSerializable(typeof(Slice))]
    [JsonSerializable(typeof(Smooth))]
    [JsonSerializable(typeof(TornEdge))]
    [JsonSerializable(typeof(WaveEdge))]
    internal sealed partial class ImageEffectsLibJsonContext : JsonSerializerContext
    {
        // Registered with ShareXJson by the ShareX.Roslyn generated ShareXGeneratedRegistration class.
    }
}
