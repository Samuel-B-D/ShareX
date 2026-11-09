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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ShareX.HelpersLib
{
    public enum TaskbarProgressBarStatus
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    // Source generated COM interface instead of [ComImport]: built-in COM interop is not available under NativeAOT
    // (BuiltInComInteropSupport=false), so creating the old [ComImport] coclass threw NotSupportedException.
    [GeneratedComInterface, Guid("c43dc798-95d1-4bea-9030-bb99e2983a1a")]
    internal partial interface ITaskbarList4
    {
        // ITaskbarList
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(IntPtr hwnd);

        [PreserveSig]
        int DeleteTab(IntPtr hwnd);

        [PreserveSig]
        int ActivateTab(IntPtr hwnd);

        [PreserveSig]
        int SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        [PreserveSig]
        int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        [PreserveSig]
        int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);

        [PreserveSig]
        int SetProgressState(IntPtr hwnd, TaskbarProgressBarStatus tbpFlags);
    }

    public static partial class TaskbarManager
    {
        private static readonly Guid CLSID_TaskbarList = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
        private static readonly Guid IID_ITaskbarList4 = new Guid("c43dc798-95d1-4bea-9030-bb99e2983a1a");
        private const uint CLSCTX_INPROC_SERVER = 0x1;

        private static readonly StrategyBasedComWrappers comWrappers = new StrategyBasedComWrappers();

        [LibraryImport("ole32.dll")]
        private static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

        private static ITaskbarList4 CreateTaskbarList()
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(CLSID_TaskbarList, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_ITaskbarList4, out IntPtr pointer));

            try
            {
                return (ITaskbarList4)comWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        private static readonly object syncLock = new object();

        private static ITaskbarList4 taskbarList;

        private static ITaskbarList4 TaskbarList
        {
            get
            {
                if (taskbarList == null)
                {
                    lock (syncLock)
                    {
                        if (taskbarList == null)
                        {
                            taskbarList = CreateTaskbarList();
                            taskbarList.HrInit();
                        }
                    }
                }

                return taskbarList;
            }
        }

        private static IntPtr mainWindowHandle;

        private static void InvokeTaskbar(Action<ITaskbarList4> action)
        {
            lock (syncLock)
            {
                try
                {
                    action(TaskbarList);
                }
                catch (InvalidComObjectException)
                {
                    // Upload progress can be reported by different COM apartments.
                    // Recreate a cached RCW whose creating apartment was torn down.
                    taskbarList = null;

                    try
                    {
                        action(TaskbarList);
                    }
                    catch (InvalidComObjectException)
                    {
                        taskbarList = null;
                    }
                    catch (FileNotFoundException)
                    {
                        Enabled = false;
                    }
                }
                catch (FileNotFoundException)
                {
                    Enabled = false;
                }
            }
        }

        private static IntPtr MainWindowHandle
        {
            get
            {
                if (mainWindowHandle == IntPtr.Zero)
                {
                    Process currentProcess = Process.GetCurrentProcess();

                    if (currentProcess == null || currentProcess.MainWindowHandle == IntPtr.Zero)
                    {
                        mainWindowHandle = IntPtr.Zero;
                    }
                    else
                    {
                        mainWindowHandle = currentProcess.MainWindowHandle;
                    }
                }

                return mainWindowHandle;
            }
        }

        public static bool Enabled { get; set; }

        public static bool IsPlatformSupported
        {
            get
            {
                return Helpers.IsWindows7OrGreater();
            }
        }

        private static void SetProgressValue(IntPtr hwnd, int currentValue, int maximumValue = 100)
        {
            if (Enabled && IsPlatformSupported && hwnd != IntPtr.Zero)
            {
                currentValue = currentValue.Clamp(0, maximumValue);

                InvokeTaskbar(taskbar => taskbar.SetProgressValue(
                    hwnd,
                    Convert.ToUInt32(currentValue),
                    Convert.ToUInt32(maximumValue)));
            }
        }

        public static void SetProgressValue(int currentValue, int maximumValue = 100)
        {
            SetProgressValue(MainWindowHandle, currentValue, maximumValue);
        }

        private static void SetProgressState(IntPtr hwnd, TaskbarProgressBarStatus state)
        {
            if (Enabled && IsPlatformSupported && hwnd != IntPtr.Zero)
            {
                InvokeTaskbar(taskbar => taskbar.SetProgressState(hwnd, state));
            }
        }

        public static void SetProgressState(TaskbarProgressBarStatus state)
        {
            SetProgressState(MainWindowHandle, state);
        }

    }
}
