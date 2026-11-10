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

using Avalonia.Threading;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Makes DWM compose a new frame on each monitor without changing anything for other applications, so a new Desktop
    /// Duplication delivers the current desktop image right away, even on a static screen.
    /// <para>
    /// Each monitor has a 1x1 window in a corner that is nearly fully transparent (alpha 1 or 2 out of 255), excluded
    /// from screen capture, click-through (not hit-testable, so the window under the mouse never changes and no
    /// application gets a mouse move or mouse leave), never activated and too small to occlude anything. Changing its
    /// alpha is enough for DWM to compose the monitor again. Popups, menus, tooltips and hover previews of other
    /// applications therefore stay open, which they would not do if a capture window was shown before the frame.
    /// </para>
    /// Each thread has its own windows and must keep pumping their messages, so <see cref="Trigger"/> only works on the
    /// UI thread and on threads that called <see cref="RegisterMessageThread"/>.
    /// </summary>
    internal static unsafe partial class CompositionTrigger
    {
        private const uint WsPopup = 0x80000000;
        private const uint WsExTopmost = 0x00000008, WsExTransparent = 0x00000020, WsExToolWindow = 0x00000080,
            WsExLayered = 0x00080000, WsExNoActivate = 0x08000000;
        private const uint LwaAlpha = 0x2;
        private const uint WdaExcludeFromCapture = 0x11;
        private const int SwHide = 0;
        private const uint SwpNoSize = 0x0001, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpNoOwnerZOrder = 0x0200;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);

        private static readonly object classLock = new object();
        private static IntPtr className;
        private static volatile bool failed;

        [ThreadStatic] private static List<IntPtr> windows;
        [ThreadStatic] private static byte alpha;
        [ThreadStatic] private static bool messageThread;

        /// <summary>The calling thread pumps window messages for as long as it runs, so it can own trigger windows.</summary>
        public static void RegisterMessageThread()
        {
            messageThread = true;
        }

        /// <summary>
        /// Makes DWM compose each of <paramref name="monitors"/> (screen pixels). Returns false when it cannot (not on the
        /// UI thread or a registered thread, window creation failed); the caller then waits for a frame composed for
        /// another reason.
        /// </summary>
        public static bool Trigger(IReadOnlyList<Rectangle> monitors)
        {
            if (failed || monitors.Count == 0 || !(messageThread || Dispatcher.UIThread.CheckAccess()))
            {
                return false;
            }

            windows ??= new List<IntPtr>();

            try
            {
                while (windows.Count < monitors.Count)
                {
                    IntPtr window = CreateTriggerWindow();

                    if (window == IntPtr.Zero)
                    {
                        failed = true;
                        DebugHelper.WriteLine($"Composition trigger window creation failed ({Marshal.GetLastPInvokeError()}).");
                        return false;
                    }

                    windows.Add(window);
                }

                // Windows are created with alpha 1, so the first value on a thread (alpha is 0 before) is 2.
                alpha = alpha == 2 ? (byte)1 : (byte)2;

                for (int i = 0; i < windows.Count; i++)
                {
                    if (i >= monitors.Count)
                    {
                        ShowWindow(windows[i], SwHide);
                        continue;
                    }

                    Rectangle monitor = monitors[i];
                    SetWindowPos(windows[i], HwndTopmost, monitor.Right - 1, monitor.Bottom - 1, 1, 1,
                        SwpNoSize | SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder);
                    SetLayeredWindowAttributes(windows[i], 0, alpha, LwaAlpha);
                }

                return true;
            }
            catch (Exception e)
            {
                failed = true;
                DebugHelper.WriteException(e, "Composition trigger failed.");
                return false;
            }
        }

        private static IntPtr CreateTriggerWindow()
        {
            IntPtr instance = GetModuleHandleW(null);

            lock (classLock)
            {
                if (className == IntPtr.Zero && !RegisterWindowClass(instance))
                {
                    return IntPtr.Zero;
                }
            }

            IntPtr window = CreateWindowExW(WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopmost,
                className, IntPtr.Zero, WsPopup, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            if (window != IntPtr.Zero)
            {
                SetLayeredWindowAttributes(window, 0, 1, LwaAlpha);
                SetWindowDisplayAffinity(window, WdaExcludeFromCapture);
            }

            return window;
        }

        // Under classLock.
        private static bool RegisterWindowClass(IntPtr instance)
        {
            IntPtr name = Marshal.StringToHGlobalUni("ShareX.CompositionTrigger");
            IntPtr user32 = NativeLibrary.Load("user32.dll");

            WndClassEx windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WindowProcedure = NativeLibrary.GetExport(user32, "DefWindowProcW"),
                Instance = instance,
                ClassName = name
            };

            if (RegisterClassExW(&windowClass) == 0)
            {
                Marshal.FreeHGlobal(name);
                return false;
            }

            className = name;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public IntPtr WindowProcedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public IntPtr MenuName;
            public IntPtr ClassName;
            public IntPtr SmallIcon;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial ushort RegisterClassExW(WndClassEx* windowClass);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr CreateWindowExW(uint exStyle, IntPtr className, IntPtr windowName, uint style, int x, int y,
            int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hwnd, int command);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr GetModuleHandleW(string moduleName);
    }
}
