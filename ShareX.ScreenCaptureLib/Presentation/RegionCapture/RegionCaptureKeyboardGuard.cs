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
/// <para>
/// The one exception is the Alt key when a region capture hotkey uses Alt. The foreground application sees Alt before
/// the hotkey key, and Alt closes menus, context menus, dropdowns and tooltips, so by the time of the hotkey they are
/// gone. On the Alt key down (not its repeats), the callback first takes a Desktop Duplication frame of the screen
/// (DesktopDuplicationCapture.Precapture, bounded by <see cref="PrecaptureTimeoutMilliseconds"/>; about one display
/// refresh), then lets the key through. The hotkey key claims that frame and the capture uses it; any other key, or
/// releasing Alt, drops it. Alt therefore reaches applications up to one refresh later.
/// </para>
/// <para>
/// The hook also runs the hotkeys Windows keeps for itself (<see cref="SetHookHotkeys"/>, Win+Shift+S) and lets them be
/// recorded (<see cref="RecordWinShortcuts"/>): it sees their key before Windows does, so consuming it is enough.
/// </para>
/// </summary>
public static unsafe partial class RegionCaptureKeyboardGuard
{
    private const int WhKeyboardLl = 13;
    private const uint MonitorDefaultToNearest = 2;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmQuit = 0x0012;
    // Thread messages of the hook thread.
    private const int WmAppEscape = 0x8001, WmAppInstallHook = 0x8002, WmAppHookHotkey = 0x8003, WmAppRecordedShortcut = 0x8004,
        WmAppMaskWinKey = 0x8005;
    private const int VkMask = 0xE8; // Unassigned virtual key, sent so that releasing Win does not open Start.
    private static readonly IntPtr InjectedMarker = new IntPtr(0x53485258);
    private const int VkEscape = 0x1B, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;
    private const int VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;
    private const int PrecaptureTimeoutMilliseconds = 40;

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

    // A region capture hotkey uses Alt (and also Ctrl, which AltGr adds).
    private static volatile bool precaptureOnAlt;
    private static volatile bool precaptureOnAltGr;

    // Hotkeys Windows keeps for itself (Win+Shift+S...), handled here instead of RegisterHotKey.
    private static volatile HookHotkey[] hookHotkeys = [];
    private static volatile bool recordWinShortcuts;

    // Hook thread only.
    private static int consumedKey; // Key whose down was consumed: its repeats and its release are consumed too.
    private static bool escapeDownConsumed;
    private static bool leftAltDown;
    private static bool rightAltDown;
    private static IntPtr hook;

    private readonly record struct ArmingHotkey(int VirtualKey, Modifiers Modifiers);

    private readonly record struct HookHotkey(int VirtualKey, Modifiers Modifiers, int Token);

    /// <summary>Raised on the UI thread with the token of a keyboard hook hotkey (see <see cref="SetHookHotkeys"/>).</summary>
    public static event Action<int>? HookHotkeyPressed;

    /// <summary>
    /// Raised on the UI thread, while <see cref="RecordWinShortcuts"/> is on, with the key (virtual key code) and modifiers
    /// of a Win shortcut. The key never reached Windows or any application.
    /// </summary>
    public static event Action<int, Modifiers>? WinShortcutRecorded;

    /// <summary>
    /// Hotkeys handled by this hook rather than RegisterHotKey: Win shortcuts that Windows keeps for itself (Win+Shift+S
    /// opens the Snipping Tool), so RegisterHotKey fails for them. When the hotkey is pressed (and the hotkeys can run,
    /// see <see cref="Start"/>), its key is consumed before Windows sees it, Win is masked so that releasing it does not
    /// open Start, and <see cref="HookHotkeyPressed"/> is raised with its token.
    /// </summary>
    public static void SetHookHotkeys(IEnumerable<(int VirtualKey, Modifiers Modifiers, int Token)> hotkeys)
    {
        hookHotkeys = hotkeys.Select(x => new HookHotkey(x.VirtualKey, x.Modifiers, x.Token)).ToArray();
    }

    /// <summary>
    /// While a hotkey is being recorded: Win shortcuts are consumed and reported with <see cref="WinShortcutRecorded"/>,
    /// otherwise Windows would act on them (Win+Shift+S) and the recording window would never see the key.
    /// </summary>
    public static bool RecordWinShortcuts
    {
        get => recordWinShortcuts;
        set => recordWinShortcuts = value;
    }

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
        ArmingHotkey[] list = hotkeys.Select(x => new ArmingHotkey(x.VirtualKey, x.Modifiers)).Distinct().ToArray();
        precaptureOnAlt = list.Any(x => x.Modifiers.HasFlag(Modifiers.Alt));
        precaptureOnAltGr = list.Any(x => x.Modifiers.HasFlag(Modifiers.Alt) && x.Modifiers.HasFlag(Modifiers.Control));
        armingHotkeys = list;
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

            if (!confirmed)
            {
                // No capture started: a frame claimed by the hotkey key is for nothing.
                DesktopDuplicationCapture.DiscardPrecapture();
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

        DesktopDuplicationCapture.DiscardPrecapture();
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

            // The Alt precapture triggers a composition from this thread, which pumps messages for its windows.
            CompositionTrigger.RegisterMessageThread();
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
            else if (message.Hwnd == IntPtr.Zero && message.Message == WmAppHookHotkey)
            {
                int token = (int)message.WParam;
                Dispatcher.UIThread.Post(() => HookHotkeyPressed?.Invoke(token), DispatcherPriority.Send);
            }
            else if (message.Hwnd == IntPtr.Zero && message.Message == WmAppRecordedShortcut)
            {
                int virtualKey = (int)message.WParam;
                Modifiers modifiers = (Modifiers)(int)message.LParam;
                Dispatcher.UIThread.Post(() => WinShortcutRecorded?.Invoke(virtualKey, modifiers), DispatcherPriority.Send);
            }
            else if (message.Hwnd == IntPtr.Zero && message.Message == WmAppMaskWinKey)
            {
                SendMaskKey();
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
        if (data->ExtraInfo == InjectedMarker)
        {
            // The mask key sent by this thread.
            return false;
        }

        int virtualKey = (int)data->VkCode;
        bool keyDown = message is WmKeyDown or WmSysKeyDown;

        if (consumedKey != 0 && virtualKey == consumedKey)
        {
            // Repeats and release of a hotkey key consumed here: nothing saw the press.
            if (!keyDown)
            {
                consumedKey = 0;
            }

            return true;
        }

        if (virtualKey == VkEscape)
        {
            if (!keyDown)
            {
                // The release of a consumed Escape is consumed too: nothing saw the press.
                bool consumed = escapeDownConsumed;
                escapeDownConsumed = false;
                return consumed;
            }

            Modifiers modifiers = GetModifiers();

            if (modifiers.HasFlag(Modifiers.Win))
            {
                DesktopDuplicationCapture.DiscardUnclaimedPrecapture();
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
                    // Escape reaches the application (Alt+Escape switches windows): a frame taken on Alt is out of date.
                    DesktopDuplicationCapture.DiscardUnclaimedPrecapture();
                    return false;
                }

                cancelRequested = true;
            }

            escapeDownConsumed = true;
            PostThreadMessageW(threadId, WmAppEscape, 0, 0);
            return true;
        }

        if (virtualKey is VkLMenu or VkRMenu or VkMenu)
        {
            HandleAlt(virtualKey, keyDown);
            return false;
        }

        if (keyDown)
        {
            ArmingHotkey[] hotkeys = armingHotkeys;
            bool hotkeyMatched = false;

            for (int i = 0; i < hotkeys.Length; i++)
            {
                if (hotkeys[i].VirtualKey == virtualKey && hotkeys[i].Modifiers == GetModifiers())
                {
                    hotkeyMatched = true;

                    if (canArmFromKeyboard?.Invoke() != false)
                    {
                        // The frame taken when Alt was pressed is the screenshot of the capture this key starts.
                        DesktopDuplicationCapture.ClaimPrecapture();

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

            if (!hotkeyMatched && !IsModifierKey(virtualKey))
            {
                // Something else than the hotkey after Alt (Alt+Tab, a menu shortcut...): the frame is out of date.
                DesktopDuplicationCapture.DiscardUnclaimedPrecapture();
            }

            if (!IsModifierKey(virtualKey) && HandleWindowsReservedShortcut(virtualKey))
            {
                return true;
            }
        }

        return false;
    }

    // Hook thread: a Win shortcut being recorded, or a hotkey Windows keeps for itself. Consumes the key down when so.
    private static bool HandleWindowsReservedShortcut(int virtualKey)
    {
        Modifiers modifiers = GetModifiers();

        if (recordWinShortcuts && modifiers.HasFlag(Modifiers.Win))
        {
            consumedKey = virtualKey;
            PostThreadMessageW(threadId, WmAppRecordedShortcut, virtualKey, (int)modifiers);
            PostThreadMessageW(threadId, WmAppMaskWinKey, 0, 0);
            return true;
        }

        HookHotkey[] hotkeys = hookHotkeys;

        for (int i = 0; i < hotkeys.Length; i++)
        {
            if (hotkeys[i].VirtualKey == virtualKey && hotkeys[i].Modifiers == modifiers)
            {
                if (canArmFromKeyboard?.Invoke() == false)
                {
                    // ShareX would not run it (hotkeys ignored, fullscreen rule): Windows keeps its shortcut.
                    return false;
                }

                consumedKey = virtualKey;
                PostThreadMessageW(threadId, WmAppHookHotkey, hotkeys[i].Token, 0);

                if (modifiers.HasFlag(Modifiers.Win))
                {
                    PostThreadMessageW(threadId, WmAppMaskWinKey, 0, 0);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends a press of an unassigned key (hook thread, outside the callback). Windows opens Start when Win is released
    /// with no other key pressed in between; the key of the shortcut was consumed, so this one stands in for it.
    /// </summary>
    private static void SendMaskKey()
    {
        Input* inputs = stackalloc Input[2];
        inputs[0] = default;
        inputs[0].Type = InputKeyboard;
        inputs[0].Union.Keyboard = new KeyboardInput { VirtualKey = VkMask, ExtraInfo = InjectedMarker };
        inputs[1] = inputs[0];
        inputs[1].Union.Keyboard.Flags = KeyEventFKeyUp;

        if (SendInput(2, inputs, sizeof(Input)) != 2)
        {
            DebugHelper.WriteLine($"Keyboard hook: SendInput failed ({Marshal.GetLastPInvokeError()}).");
        }
    }

    private const uint InputKeyboard = 1, KeyEventFKeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // Only gives the union the size of its largest member.
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input* inputs, int size);

    private static bool IsModifierKey(int virtualKey) => virtualKey is VkShift or VkControl or VkMenu or VkLWin or VkRWin
        or VkLShift or VkRShift or VkLControl or VkRControl or VkLMenu or VkRMenu;

    // Hook thread, before the Alt key reaches any application.
    private static void HandleAlt(int virtualKey, bool keyDown)
    {
        bool right = virtualKey == VkRMenu;

        if (!keyDown)
        {
            if (right) rightAltDown = false;
            else leftAltDown = false;

            // Alt released without the hotkey key.
            DesktopDuplicationCapture.DiscardUnclaimedPrecapture();
            return;
        }

        bool repeat = right ? rightAltDown : leftAltDown;

        if (right) rightAltDown = true;
        else leftAltDown = true;

        if (repeat || !precaptureOnAlt)
        {
            return;
        }

        if (right && IsDown(VkLControl) && !precaptureOnAltGr)
        {
            // AltGr (typing a character on many keyboard layouts), and no capture hotkey uses Ctrl+Alt.
            return;
        }

        lock (stateLock)
        {
            if (armed && confirmed)
            {
                // A capture is open: Alt is for it.
                return;
            }
        }

        if (canArmFromKeyboard?.Invoke() == false)
        {
            return;
        }

        DesktopDuplicationCapture.Precapture(PrecaptureTimeoutMilliseconds);
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
