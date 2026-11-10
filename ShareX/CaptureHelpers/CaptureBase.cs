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
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using System;
using System.Drawing;
using System.Threading.Tasks;

namespace ShareX
{
    public abstract class CaptureBase
    {
        public bool AllowAutoHideForm { get; set; } = true;
        public bool AllowAnnotation { get; set; } = true;

        public void Capture(bool autoHideForm)
        {
            Capture(null, autoHideForm);
        }

        public void Capture(TaskSettings taskSettings = null, bool autoHideForm = false)
        {
            if (taskSettings == null) taskSettings = TaskSettings.GetDefaultTaskSettings();

            _ = CaptureAsync(taskSettings, autoHideForm);
        }

        protected abstract TaskMetadata Execute(TaskSettings taskSettings);

        /// <summary>Called on the UI thread right before the capture starts (after a configured screenshot delay).</summary>
        protected virtual void OnCaptureStarting()
        {
        }

        /// <summary>Called when the capture failed with an exception.</summary>
        protected virtual void OnCaptureAborted()
        {
        }

        protected virtual Task<TaskMetadata> ExecuteAsync(TaskSettings taskSettings)
        {
            return Task.FromResult(Execute(taskSettings));
        }

        private async Task CaptureAsync(TaskSettings taskSettings, bool autoHideForm)
        {
            // One capture at a time while the region capture is open: a screenshot taken now would contain its overlay
            // (the capture hotkey pressed again, another capture hotkey...).
            if (RegionCaptureIntegration.IsCaptureInProgress)
            {
                DebugHelper.WriteLine("Capture ignored: a region capture is in progress.");
                return;
            }

            try
            {
                if (taskSettings.CaptureSettings.ScreenshotDelay > 0)
                {
                    int delay = (int)(taskSettings.CaptureSettings.ScreenshotDelay * 1000);

                    if (taskSettings.UseDefaultCaptureSettings && ApplicationState.Settings.ScreenshotDelayJustOnce)
                    {
                        MainWindowIntegration.SetScreenshotDelay(0);
                    }

                    await Task.Delay(delay);

                    if (RegionCaptureIntegration.IsCaptureInProgress)
                    {
                        DebugHelper.WriteLine("Capture ignored: a region capture is in progress.");
                        return;
                    }
                }

                // Runs synchronously in the hotkey message when there is no screenshot delay (no await before it).
                OnCaptureStarting();
                await CaptureInternalAsync(taskSettings, autoHideForm);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
                OnCaptureAborted();
            }
        }

        private async Task CaptureInternalAsync(TaskSettings taskSettings, bool autoHideForm)
        {
            bool wait = false;
            bool showDesktopIcons = false;
            bool showMainForm = false;

            if (taskSettings.GeneralSettings.ToastWindowAutoHide)
            {
                await NotificationWindow.CloseActiveWindowAsync();
            }

            if (taskSettings.CaptureSettings.CaptureAutoHideDesktopIcons && !CaptureHelpers.IsActiveWindowFullscreen() && DesktopIconManager.AreDesktopIconsVisible())
            {
                DesktopIconManager.SetDesktopIconsVisibility(false);
                showDesktopIcons = true;
                wait = true;
            }

            if (autoHideForm && AllowAutoHideForm)
            {
                MainWindowIntegration.Hide();
                showMainForm = true;
                wait = true;
            }

            if (wait)
            {
                await Task.Delay(250);
            }

            TaskMetadata metadata = null;

            try
            {
                AllowAnnotation = true;
                metadata = await ExecuteAsync(taskSettings);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
            }
            finally
            {
                if (showDesktopIcons)
                {
                    DesktopIconManager.SetDesktopIconsVisibility(true);
                }

                if (showMainForm)
                {
                    MainWindowIntegration.Activate();
                }

                AfterCapture(metadata, taskSettings);
            }
        }

        private void AfterCapture(TaskMetadata metadata, TaskSettings taskSettings)
        {
            if (metadata != null && metadata.Image != null)
            {
                TaskHelpers.PlayNotificationSoundAsync(NotificationSound.Capture, taskSettings);

                if (taskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.AnnotateImage) && !AllowAnnotation)
                {
                    taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.AnnotateImage);
                }

                if (taskSettings.ImageSettings.ImageEffectOnlyRegionCapture &&
                    GetType() != typeof(CaptureRegion) && GetType() != typeof(CaptureLastRegion) &&
                    GetType() != typeof(CaptureRegionTransparent))
                {
                    taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.AddImageEffects);
                }

                UploadManager.RunImageTask(metadata, taskSettings);
            }
        }

        protected TaskMetadata CreateMetadata()
        {
            return CreateMetadata(Rectangle.Empty, null);
        }

        protected TaskMetadata CreateMetadata(Rectangle insideRect)
        {
            return CreateMetadata(insideRect, "explorer");
        }

        protected TaskMetadata CreateMetadata(Rectangle insideRect, string ignoreProcess)
        {
            TaskMetadata metadata = new TaskMetadata();

            IntPtr handle = NativeMethods.GetForegroundWindow();
            WindowInfo windowInfo = new WindowInfo(handle);

            if ((ignoreProcess == null || !windowInfo.ProcessName.Equals(ignoreProcess, StringComparison.OrdinalIgnoreCase)) &&
                (insideRect.IsEmpty || windowInfo.Rectangle.Contains(insideRect)))
            {
                metadata.UpdateInfo(windowInfo);
            }

            return metadata;
        }
    }
}
