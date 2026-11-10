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
using ShareX.ScreenCaptureLib;
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using SkiaSharp;
using System.Drawing;
using System.Threading.Tasks;
using Bitmap = SkiaSharp.SKBitmap;

namespace ShareX
{
    public class CaptureRegion : CaptureBase
    {
        protected override TaskMetadata Execute(TaskSettings taskSettings)
        {
            return null;
        }

        protected override void OnCaptureStarting()
        {
            // From the first moment of the capture flow, Escape cancels it whenever it is pressed, and no other capture
            // starts until it ends (ExecuteAsync or OnCaptureAborted).
            RegionCaptureIntegration.BeginCaptureInput();
        }

        protected override void OnCaptureAborted()
        {
            RegionCaptureIntegration.ConcealPrewarmedWindow();
            RegionCaptureIntegration.EndCaptureInput();
        }

        protected sealed override async Task<TaskMetadata> ExecuteAsync(TaskSettings taskSettings)
        {
            try
            {
                return await ExecuteCaptureAsync(taskSettings);
            }
            finally
            {
                // Every path out of the capture goes through here.
                RegionCaptureIntegration.EndCaptureInput();
            }
        }

        protected virtual Task<TaskMetadata> ExecuteCaptureAsync(TaskSettings taskSettings)
        {
            return ExecuteRegionCaptureAvaloniaAsync(taskSettings);
        }

        protected async Task<TaskMetadata> ExecuteRegionCaptureAvaloniaAsync(TaskSettings taskSettings)
        {
            // Normally already started in OnCaptureStarting (keeps its state); ended by ExecuteAsync.
            RegionCaptureIntegration.BeginCaptureInput();

            if (RegionCaptureIntegration.IsCancelRequested)
            {
                // Escape was pressed right after the hotkey: do not even take the screenshot.
                return null;
            }

            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
            screenshot.CaptureCursor = false;

            bool activeMonitorMode = taskSettings.CaptureSettings.RegionCaptureOptions.ActiveMonitorMode;
            Rectangle screenBounds = activeMonitorMode
                ? CaptureHelpers.GetActiveScreenBounds()
                : CaptureHelpers.GetScreenBounds();

            // Nothing is shown before the screenshot is taken: a window appearing over other applications (even one
            // excluded from capture) changes the window under the mouse, and their popups, menus, tooltips and hover
            // previews close before the frame is composed.

            // The capture is a new bitmap owned by the request, no extra copy needed.
            SKBitmap frozenScreenshot = null;
            SKBitmap cursorBitmap = null;
            Point cursorPosition = Point.Empty;

            try
            {
                frozenScreenshot = activeMonitorMode
                    ? screenshot.CaptureActiveMonitor()
                    : screenshot.CaptureFullscreen();

                if (frozenScreenshot == null || RegionCaptureIntegration.IsCancelRequested)
                {
                    // No screenshot, or Escape was pressed while it was taken.
                    frozenScreenshot?.Dispose();
                    RegionCaptureIntegration.ConcealPrewarmedWindow();
                    return null;
                }

                // The screenshot exists: show the overlay right away, before the capture is attached to it.
                RegionCaptureIntegration.ShowPrewarmedOverlay(screenBounds);

                if (taskSettings.CaptureSettings.ShowCursor)
                {
                    CursorData cursorData = new CursorData();
                    if (cursorData.IsVisible)
                    {
                        cursorBitmap = cursorData.ToBitmap(frozenScreenshot, screenBounds.Location);
                        cursorPosition = new Point(
                            cursorData.DrawPosition.X - screenBounds.X,
                            cursorData.DrawPosition.Y - screenBounds.Y);
                    }
                }
            }
            catch
            {
                // The overlay may already be shown over the live desktop; never leave it on screen without a capture.
                RegionCaptureIntegration.ConcealPrewarmedWindow();
                frozenScreenshot?.Dispose();
                cursorBitmap?.Dispose();
                throw;
            }

            AvaloniaRegionCaptureRequest request = new AvaloniaRegionCaptureRequest
            {
                Screenshot = frozenScreenshot,
                // Screen captures are opaque: WindowsImageInterop.Capture and HDR correction set alpha to 255.
                ScreenshotIsOpaque = true,
                ScreenBounds = screenBounds,
                RegionCaptureOptions = taskSettings.CaptureSettingsReference.RegionCaptureOptions,
                ImageEditorOptions = taskSettings.ToolsSettingsReference.ImageEditorOptions,
                EnableAnnotations = !taskSettings.AdvancedSettings.RegionCaptureDisableAnnotation,
                AllowMultipleRegions = true,
                CursorBitmap = cursorBitmap,
                CursorPosition = cursorPosition
            };

            AvaloniaRegionCaptureResult result = await RegionCaptureIntegration.CaptureAsync(request);
            if (result == null)
            {
                return null;
            }

            using (result.Image)
            {
                Bitmap output = result.Image.Copy();
                TaskMetadata metadata = new TaskMetadata(output);

                if (result.ImageModified)
                {
                    AllowAnnotation = false;
                }

                if (result.WindowInfo != null)
                {
                    metadata.UpdateInfo(result.WindowInfo);
                }

                return metadata;
            }
        }
    }
}
