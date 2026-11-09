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

using System;
using System.ComponentModel;
using System.Drawing;

namespace ShareX.HelpersLib
{
    // Replacements for [DefaultValue(typeof(T), "text")]. That constructor converts the text with
    // TypeDescriptor.GetConverter(type) at runtime, which trimming disables by default
    // (System.ComponentModel.DefaultValueAttribute.IsSupported=false) because it is not trimming safe.
    // These attributes convert with a known TypeConverter instead, so the default value is the same.

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class DefaultColorAttribute : DefaultValueAttribute
    {
        public DefaultColorAttribute(string color) : base(new ColorConverter().ConvertFromInvariantString(color))
        {
        }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class DefaultPointAttribute : DefaultValueAttribute
    {
        public DefaultPointAttribute(int x, int y) : base(new Point(x, y))
        {
        }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class DefaultSizeAttribute : DefaultValueAttribute
    {
        public DefaultSizeAttribute(int width, int height) : base(new Size(width, height))
        {
        }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class DefaultImageFontAttribute : DefaultValueAttribute
    {
        public DefaultImageFontAttribute(string font) : base(new ImageFontConverter().ConvertFromInvariantString(font))
        {
        }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class DefaultImageMarginsAttribute : DefaultValueAttribute
    {
        public DefaultImageMarginsAttribute(int left, int top, int right, int bottom) : base(new ImageMargins(left, top, right, bottom))
        {
        }
    }
}
