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

using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using System;
using System.Collections.Generic;
using System.Linq;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    public class HotkeyManager
    {
        public List<HotkeySettings> Hotkeys { get; private set; }
        public bool IgnoreHotkeys { get; set; }

        public delegate void HotkeyTriggerEventHandler(HotkeySettings hotkeySetting);
        public delegate void HotkeysToggledEventHandler(bool hotkeysEnabled);

        public HotkeyTriggerEventHandler HotkeyTrigger;
        public HotkeysToggledEventHandler HotkeysToggledTrigger;

        private IHotkeyHost hotkeyHost;

        // Win hotkeys that Windows keeps for itself (RegisterHotKey fails, e.g. Win+Shift+S), run by the keyboard hook.
        private readonly Dictionary<int, HotkeySettings> keyboardHookHotkeys = new Dictionary<int, HotkeySettings>();
        private int lastKeyboardHookToken;

        public HotkeyManager(IHotkeyHost host)
        {
            hotkeyHost = host;
            hotkeyHost.HotkeyPress += OnHotkeyPressed;
            hotkeyHost.Closed += OnHostClosed;

            // Escape must cancel a region capture whenever it is pressed after the hotkey, even while the UI thread is
            // busy taking the screenshot. The guard sees the hotkey key press itself on its own thread; this mirrors the
            // conditions under which OnHotkeyPressed runs the hotkey. It runs on the guard thread, so it must not touch the
            // UI thread (CaptureHelpers.IsActiveWindowFullscreen does, through DesktopScreen).
            RegionCaptureKeyboardGuard.Start(() => !IgnoreHotkeys &&
                (!ApplicationState.Settings.DisableHotkeysOnFullscreen || !RegionCaptureKeyboardGuard.IsForegroundWindowFullscreen()));
            RegionCaptureKeyboardGuard.HookHotkeyPressed += OnKeyboardHookHotkeyPressed;
        }

        private void OnKeyboardHookHotkeyPressed(int token)
        {
            keyboardHookHotkeys.TryGetValue(token, out HotkeySettings hotkeySetting);
            HandleHotkeyPress(hotkeySetting);
        }

        private bool IsKeyboardHookHotkey(HotkeySettings hotkeySetting, out int token)
        {
            foreach (KeyValuePair<int, HotkeySettings> pair in keyboardHookHotkeys)
            {
                if (pair.Value == hotkeySetting)
                {
                    token = pair.Key;
                    return true;
                }
            }

            token = 0;
            return false;
        }

        private void UpdateKeyboardHookHotkeys()
        {
            RegionCaptureKeyboardGuard.SetHookHotkeys(keyboardHookHotkeys
                .Select(x => ((int)x.Value.HotkeyInfo.KeyCode, x.Value.HotkeyInfo.ModifiersEnum, x.Key)));
        }

        /// <summary>Tells the region capture keyboard guard which registered hotkeys open the region capture.</summary>
        private void UpdateRegionCaptureKeyboardGuard()
        {
            RegionCaptureKeyboardGuard.SetArmingHotkeys(Hotkeys
                .Where(x => x.HotkeyInfo.Status == HotkeyStatus.Registered && x.TaskSettings.Job == HotkeyType.RectangleRegion)
                .Select(x => ((int)x.HotkeyInfo.KeyCode, x.HotkeyInfo.ModifiersEnum)));
        }

        private void OnHotkeyPressed(ushort id, InputKey key, Modifiers modifier)
        {
            HandleHotkeyPress(Hotkeys.Find(x => x.HotkeyInfo.ID == id));
        }

        private void HandleHotkeyPress(HotkeySettings hotkeySetting)
        {
            if (!IgnoreHotkeys && (!ApplicationState.Settings.DisableHotkeysOnFullscreen || !CaptureHelpers.IsActiveWindowFullscreen()))
            {
                if (hotkeySetting != null)
                {
                    OnHotkeyTrigger(hotkeySetting);
                }
            }

            // A region capture started by this hotkey has confirmed the Escape guard by now (synchronously, see
            // CaptureBase.CaptureAsync); otherwise the arm made by the key press is dropped.
            RegionCaptureKeyboardGuard.ReleaseUnconfirmed();
        }

        private void OnHostClosed(object sender, EventArgs e)
        {
            if (hotkeyHost != null && !hotkeyHost.IsDisposed)
            {
                UnregisterAllHotkeys(false);
            }
        }

        public void UpdateHotkeys(List<HotkeySettings> hotkeys, bool showFailedHotkeys)
        {
            if (Hotkeys != null)
            {
                UnregisterAllHotkeys();
            }

            Hotkeys = hotkeys;

            RegisterAllHotkeys();

            if (showFailedHotkeys)
            {
                ShowFailedHotkeys();
            }
        }

        protected void OnHotkeyTrigger(HotkeySettings hotkeySetting)
        {
            HotkeyTrigger?.Invoke(hotkeySetting);
        }

        public void RegisterHotkey(HotkeySettings hotkeySetting)
        {
            if (!ApplicationState.Settings.DisableHotkeys || hotkeySetting.TaskSettings.Job == HotkeyType.DisableHotkeys)
            {
                UnregisterHotkey(hotkeySetting, false);

                if (hotkeySetting.HotkeyInfo.Status != HotkeyStatus.Registered && hotkeySetting.HotkeyInfo.IsValidHotkey)
                {
                    hotkeyHost.RegisterHotkey(hotkeySetting.HotkeyInfo);

                    if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Registered)
                    {
                        DebugHelper.WriteLine("Hotkey registered: " + hotkeySetting);
                    }
                    else if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Failed && hotkeySetting.HotkeyInfo.Win)
                    {
                        // A Win shortcut of Windows (Win+Shift+S...): the keyboard hook sees the key first and takes it.
                        int token = ++lastKeyboardHookToken;
                        keyboardHookHotkeys[token] = hotkeySetting;
                        hotkeySetting.HotkeyInfo.Status = HotkeyStatus.Registered;
                        UpdateKeyboardHookHotkeys();
                        DebugHelper.WriteLine("Hotkey registered with the keyboard hook (Windows shortcut overridden): " + hotkeySetting);
                    }
                    else if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Failed)
                    {
                        DebugHelper.WriteLine("Hotkey register failed: " + hotkeySetting);
                    }
                }
                else
                {
                    hotkeySetting.HotkeyInfo.Status = HotkeyStatus.NotConfigured;
                }
            }

            if (!Hotkeys.Contains(hotkeySetting))
            {
                Hotkeys.Add(hotkeySetting);
            }

            UpdateRegionCaptureKeyboardGuard();
        }

        public void RegisterAllHotkeys()
        {
            foreach (HotkeySettings hotkeySetting in Hotkeys.ToArray())
            {
                RegisterHotkey(hotkeySetting);
            }
        }

        public void RegisterFailedHotkeys()
        {
            foreach (HotkeySettings hotkeySetting in Hotkeys.Where(x => x.HotkeyInfo.Status == HotkeyStatus.Failed))
            {
                RegisterHotkey(hotkeySetting);
            }
        }

        public void UnregisterHotkey(HotkeySettings hotkeySetting, bool removeFromList = true)
        {
            if (IsKeyboardHookHotkey(hotkeySetting, out int token))
            {
                keyboardHookHotkeys.Remove(token);
                UpdateKeyboardHookHotkeys();
                hotkeySetting.HotkeyInfo.Status = HotkeyStatus.NotConfigured;
                DebugHelper.WriteLine("Hotkey unregistered from the keyboard hook: " + hotkeySetting);
            }
            else if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Registered)
            {
                hotkeyHost.UnregisterHotkey(hotkeySetting.HotkeyInfo);

                if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.NotConfigured)
                {
                    DebugHelper.WriteLine("Hotkey unregistered: " + hotkeySetting);
                }
                else if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Failed)
                {
                    DebugHelper.WriteLine("Hotkey unregister failed: " + hotkeySetting);
                }
            }

            if (removeFromList)
            {
                Hotkeys.Remove(hotkeySetting);
            }

            UpdateRegionCaptureKeyboardGuard();
        }

        public void UnregisterAllHotkeys(bool removeFromList = true, bool temporary = false)
        {
            if (Hotkeys != null)
            {
                foreach (HotkeySettings hotkeySetting in Hotkeys.ToArray())
                {
                    if (!temporary || hotkeySetting.TaskSettings.Job != HotkeyType.DisableHotkeys)
                    {
                        UnregisterHotkey(hotkeySetting, removeFromList);
                    }
                }
            }
        }

        public void ToggleHotkeys(bool hotkeysDisabled)
        {
            if (!hotkeysDisabled)
            {
                RegisterAllHotkeys();
            }
            else
            {
                UnregisterAllHotkeys(false, true);
            }

            HotkeysToggledTrigger?.Invoke(hotkeysDisabled);
        }

        public void ShowFailedHotkeys()
        {
            List<HotkeySettings> failedHotkeysList = Hotkeys.Where(x => x.HotkeyInfo.Status == HotkeyStatus.Failed).ToList();

            if (failedHotkeysList.Count > 0)
            {
                string failedHotkeys = string.Join("\r\n", failedHotkeysList.Select(x => $"[{x.HotkeyInfo}] {x.TaskSettings}"));
                string hotkeyText = failedHotkeysList.Count > 1 ? Strings.HotkeyManager_ShowFailedHotkeys_hotkeys : Strings.HotkeyManager_ShowFailedHotkeys_hotkey;
                string text = string.Format(Strings.HotkeyManager_ShowFailedHotkeys_Unable_to_register_hotkey, hotkeyText, failedHotkeys);

                MessageBox.Show(text, "ShareX - " + Strings.HotkeyManager_ShowFailedHotkeys_Hotkey_registration_failed, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void ResetHotkeys()
        {
            UnregisterAllHotkeys();
            Hotkeys.AddRange(GetDefaultHotkeyList());
            RegisterAllHotkeys();

            if (ApplicationState.Settings.DisableHotkeys)
            {
                TaskHelpers.ToggleHotkeys();
            }
        }

        public static List<HotkeySettings> GetDefaultHotkeyList()
        {
            return new List<HotkeySettings>
            {
                new HotkeySettings(HotkeyType.RectangleRegion, InputKey.Control | InputKey.PrintScreen),
                new HotkeySettings(HotkeyType.PrintScreen, InputKey.PrintScreen),
                new HotkeySettings(HotkeyType.ActiveWindow, InputKey.Alt | InputKey.PrintScreen),
                new HotkeySettings(HotkeyType.ScreenRecorder, InputKey.Shift | InputKey.PrintScreen),
                new HotkeySettings(HotkeyType.ScreenRecorderGIF, InputKey.Control | InputKey.Shift | InputKey.PrintScreen)
            };
        }
    }
}
