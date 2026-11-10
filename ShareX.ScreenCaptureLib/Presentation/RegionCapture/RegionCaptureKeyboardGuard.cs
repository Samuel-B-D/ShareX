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

using Avalonia.Threading;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShareX.ScreenCaptureLib.Presentation.RegionCapture;

/// <summary>
/// Makes Escape cancel a region capture whenever it is pressed after the capture hotkey, independently of the UI
/// thread.
/// <para>
/// The capture hotkey (RegisterHotKey) is delivered to the UI thread, which also takes the screenshot, attaches it and
/// renders the overlay, and the overlay only receives keys once it is the foreground window. Anything started from the
/// UI thread therefore leaves a gap in which Escape reaches the previously focused application (or Windows: Escape
/// pressed while Alt of Alt+PrintScreen is still down is Alt+Escape).
/// </para>
/// <para>
/// A low-level keyboard hook runs on a dedicated thread that only pumps its messages, so it sees every key as it
/// happens and never waits for another thread. When it sees the key down of a region capture hotkey it arms, before
/// Windows turns the key into the WM_HOTKEY message. The capture confirms the arm when it starts and ends it when it
/// ends (<see cref="BeginCapture"/>, <see cref="EndCapture"/>); an arm that no capture confirms (hotkey ignored by the
/// hotkey host) is released (<see cref="ReleaseUnconfirmed"/>) or expires after <see cref="UnconfirmedArmTimeout"/>.
/// </para>
/// <para>
/// For the whole time it is armed, every Escape (with any Ctrl, Alt or Shift combination) is consumed,
/// <see cref="CancelRequested"/> is set immediately and <see cref="EscapePressed"/> is raised on the UI thread, where
/// the capture handles it. Escape therefore never depends on which window has the keyboard, on Avalonia's key routing,
/// or on Windows shortcuts (Alt+Escape).
/// </para>
/// <para>
/// Windows silently removes a low-level hook whose callback is too slow (LowLevelHooksTimeout). The callback therefore
/// never waits for another thread: it reads key state, updates a few fields and posts a message to its own thread,
/// which forwards it to the UI thread. The hook is also installed again at the start and end of each capture, so a
/// hook that Windows removed anyway is back for the next key.
/// </para>
/// </summary>
public static unsafe partial class RegionCaptureKeyboardGuard
{
    private const int WhKeyboardLl = 13;
    private const uint MonitorDefaultToNearest = 2;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmQuit = 0x0012;
    // Thread messages of the hook thread.
    private const int WmAppEscape = 0x8001, WmAppInstallHook = 0x8002;
    private const int VkEscape = 0x1B, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;

    private static readonly long UnconfirmedArmTimeout = Stopwatch.Frequency * 3 / 2; // 1.5 s

    private static readonly object startLock = new object();
    private static readonly Action raiseEscapePressed = () => EscapePressed?.Invoke();
    private static Thread? thread;
    private static volatile uint threadId;

    // Shared with the hook thread. State changes are made under stateLock, which is only ever held for a few field
    // assignments (never around a call that can wait or allocate), so the hook thread is never delayed by it.
    private static readonly Lock stateLock = new Lock();
    private static volatile ArmingHotkey[] armingHotkeys = [];
    private static volatile Func<bool>? canArmFromKeyboard;
    private static bool armed;
    private static bool confirmed;
    private static volatile bool cancelRequested;
    private static long armedAt;

    // Hook thread only.
    private static bool escapeDownConsumed;
    private static IntPtr hook;

    private readonly record struct ArmingHotkey(int VirtualKey, Modifiers Modifiers);

    /// <summary>Raised on the UI thread for an Escape consumed while armed.</summary>
    public static event Action? EscapePressed;

    /// <summary>Escape was pressed since the current capture started (set immediately by the hook thread).</summary>
    public static bool CancelRequested => cancelRequested;

    /// <summary>
    /// Starts the hook thread. <paramref name="canArm"/> runs on the hook thread for each capture hotkey press and must
    /// mirror the conditions under which the hotkey host runs the hotkey (hotkeys not ignored, fullscreen rule...). It
    /// must never wait for another thread.
    /// </summary>
    public static void Start(Func<bool> canArm)
    {
        canArmFromKeyboard = canArm;

        lock (startLock)
        {
            if (thread != null)
            {
                return;
            }

            using ManualResetEventSlim started = new ManualResetEventSlim();
            thread = new Thread(() => Run(started))
            {
                Name = "ShareX region capture keyboard guard",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            thread.Start();
            started.Wait(TimeSpan.FromSeconds(5));
        }
    }

    public static void Stop()
    {
        lock (startLock)
        {
            if (thread != null && threadId != 0)
            {
                PostThreadMessageW(threadId, WmQuit, 0, 0);
            }

            thread = null;
            threadId = 0;
        }
    }

    /// <summary>Hotkeys (virtual key and modifiers) that start a region capture.</summary>
    public static void SetArmingHotkeys(IEnumerable<(int VirtualKey, Modifiers Modifiers)> hotkeys)
    {
        armingHotkeys = hotkeys.Select(x => new ArmingHotkey(x.VirtualKey, x.Modifiers)).Distinct().ToArray();
    }

    /// <summary>
    /// The capture flow started (UI thread): confirms an arm made by the hotkey key press, keeping an Escape already
    /// pressed, or arms now for captures started without the keyboard (tray, CLI). A confirmed arm lasts until
    /// <see cref="EndCapture"/>.
    /// </summary>
    public static void BeginCapture()
    {
        lock (stateLock)
        {
            if (armed && confirmed)
            {
                // Same capture (BeginCapture is called at more than one step).
                return;
            }

            if (!armed || IsExpired(Stopwatch.GetTimestamp()))
            {
                cancelRequested = false;
                armedAt = Stopwatch.GetTimestamp();
                armed = true;
            }

            confirmed = true;
        }

        ReinstallHook();
    }

    /// <summary>
    /// The hotkey message was handled without starting a capture (UI thread): drops an arm made by its key press, so
    /// Escape is not consumed for nothing. A confirmed arm is kept.
    /// </summary>
    public static void ReleaseUnconfirmed()
    {
        lock (stateLock)
        {
            if (armed && !confirmed)
            {
                armed = false;
                cancelRequested = false;
            }
        }
    }

    /// <summary>The capture flow ended.</summary>
    public static void EndCapture()
    {
        lock (stateLock)
        {
            armed = false;
            confirmed = false;
            cancelRequested = false;
        }

        ReinstallHook();
    }

    // Under stateLock.
    private static bool IsExpired(long now) => !confirmed && now - armedAt > UnconfirmedArmTimeout;

    /// <summary>Asks the hook thread to install the hook again (it may have been removed by Windows).</summary>
    private static void ReinstallHook()
    {
        uint id = threadId;

        if (id != 0)
        {
            PostThreadMessageW(id, WmAppInstallHook, 0, 0);
        }
    }

    // Hook thread. The new hook is installed before the old one is removed, so no key is missed in between.
    private static void InstallHook()
    {
        IntPtr newHook = SetWindowsHookExW(WhKeyboardLl, &HookProc, GetModuleHandleW(null), 0);

        if (newHook == IntPtr.Zero)
        {
            DebugHelper.WriteLine($"Region capture keyboard guard: SetWindowsHookEx failed ({Marshal.GetLastPInvokeError()}).");
            return;
        }

        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
        }

        hook = newHook;
    }

    private static void Run(ManualResetEventSlim started)
    {
        try
        {
            threadId = GetCurrentThreadId();
            InstallHook();
        }
        finally
        {
            started.Set();
        }

        // This thread only pumps messages, so the hook always answers immediately (a slow low-level hook delays all
        // keyboard input and is removed by Windows).
        Msg message;

        while (GetMessageW(&message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Hwnd == IntPtr.Zero && message.Message == WmAppEscape)
            {
                // Outside the hook callback: posting to the dispatcher allocates.
                Dispatcher.UIThread.Post(raiseEscapePressed, DispatcherPriority.Send);
            }
            else if (message.Hwnd == IntPtr.Zero && message.Message == WmAppInstallHook)
            {
                InstallHook();
            }
            else
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }

        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == HcAction && HandleKey((int)wParam, (KbdLlHookStruct*)lParam))
            {
                // Consumed: the key never reaches another application or a Windows shortcut.
                return 1;
            }
        }
        catch
        {
            // Never let an exception unwind into the native hook chain.
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool HandleKey(int message, KbdLlHookStruct* data)
    {
        int virtualKey = (int)data->VkCode;
        bool keyDown = message is WmKeyDown or WmSysKeyDown;

        if (virtualKey == VkEscape)
        {
            if (!keyDown)
            {
                // The release of a consumed Escape is consumed too: nothing saw the press.
                bool consumed = escapeDownConsumed;
                escapeDownConsumed = false;
                return consumed;
            }

            if (GetModifiers().HasFlag(Modifiers.Win))
            {
                return false;
            }

            lock (stateLock)
            {
                if (armed && IsExpired(Stopwatch.GetTimestamp()))
                {
                    armed = false;
                }

                if (!armed)
                {
                    return false;
                }

                cancelRequested = true;
            }

            escapeDownConsumed = true;
            PostThreadMessageW(threadId, WmAppEscape, 0, 0);
            return true;
        }

        if (keyDown)
        {
            ArmingHotkey[] hotkeys = armingHotkeys;

            for (int i = 0; i < hotkeys.Length; i++)
            {
                if (hotkeys[i].VirtualKey == virtualKey && hotkeys[i].Modifiers == GetModifiers())
                {
                    if (canArmFromKeyboard?.Invoke() != false)
                    {
                        lock (stateLock)
                        {
                            if (armed && IsExpired(Stopwatch.GetTimestamp()))
                            {
                                armed = false;
                            }

                            // Hotkey pressed again during a capture: keep that capture's state.
                            if (!armed)
                            {
                                cancelRequested = false;
                                confirmed = false;
                                armedAt = Stopwatch.GetTimestamp();
                                armed = true;
                            }
                        }
                    }

                    break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Same rule as CaptureHelpers.IsActiveWindowFullscreen (the foreground window covers its whole monitor, desktop
    /// excluded), with Win32 calls only so it can run on the guard thread without waiting for the UI thread.
    /// </summary>
    public static bool IsForegroundWindowFullscreen()
    {
        IntPtr handle = GetForegroundWindow();

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        char* className = stackalloc char[64];
        int length = GetClassNameW(handle, className, 64);
        ReadOnlySpan<char> name = new ReadOnlySpan<char>(className, Math.Max(length, 0));

        if (name.Equals("Progman", StringComparison.OrdinalIgnoreCase) || name.Equals("WorkerW", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Rect window;
        MonitorInfo monitor = new MonitorInfo { Size = sizeof(MonitorInfo) };

        if (!GetWindowRect(handle, &window) || !GetMonitorInfoW(MonitorFromWindow(handle, MonitorDefaultToNearest), &monitor))
        {
            return false;
        }

        return window.Left <= monitor.Monitor.Left && window.Top <= monitor.Monitor.Top &&
            window.Right >= monitor.Monitor.Right && window.Bottom >= monitor.Monitor.Bottom;
    }

    private static Modifiers GetModifiers()
    {
        Modifiers modifiers = Modifiers.None;
        if (IsDown(VkMenu)) modifiers |= Modifiers.Alt;
        if (IsDown(VkControl)) modifiers |= Modifiers.Control;
        if (IsDown(VkShift)) modifiers |= Modifiers.Shift;
        if (IsDown(VkLWin) || IsDown(VkRWin)) modifiers |= Modifiers.Win;
        return modifiers;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr> lpfn, IntPtr hmod, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* message, IntPtr hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Msg* message);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(Msg* message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(IntPtr hwnd, char* className, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, Rect* rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr monitor, MonitorInfo* info);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? moduleName);
}
