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

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ShareX.ScreenRecordingLib.Native;

// Built-in COM interop (Marshal.GetComInterfaceForObject) is not available under NativeAOT, so
// managed callbacks are exposed through source generated COM wrappers instead.
internal static class ComCallableWrapper
{
    private static readonly StrategyBasedComWrappers comWrappers = new();

    /// <summary>Returns an owned (AddRef'd) pointer to <paramref name="iid"/> on a [GeneratedComClass] instance.</summary>
    public static nint Create(object instance, Guid iid)
    {
        // The returned CCW roots the managed object until its last native reference is released.
        nint unknown = comWrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint pointer));
            return pointer;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
