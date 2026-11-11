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
using Microsoft.Win32;
using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using ShareX.ImageEditor.Integration;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib.Presentation.RegionCapture;

/// <summary>
/// Settings the next region capture is expected to use. A prewarmed window builds its editor view model and annotation
/// toolbar from them ahead of time; a capture with other settings objects or changed toolbar settings rebuilds them.
/// </summary>
public sealed record RegionCapturePrewarmOptions(RegionCaptureOptions RegionCaptureOptions, ImageEditorOptions ImageEditorOptions, bool EnableAnnotations);

/// <summary>UI-thread boundary for the Avalonia region capture experience.</summary>
public static class RegionCaptureIntegration
{
    /// <summary>Bounds of the last captured region, or of all regions when several were captured.</summary>
    public static Rectangle LastRegionRectangle { get; private set; }

    /// <summary>Screen rectangles of the last captured regions. Holds more than one item after a multi region capture.</summary>
    public static IReadOnlyList<Rectangle> LastRegionRectangles { get; private set; } = [];

    internal static void SetLastRegion(Rectangle bounds, IReadOnlyList<Rectangle> regions)
    {
        LastRegionRectangle = bounds;
        LastRegionRectangles = regions;
    }

    public static Task<AvaloniaRegionCaptureResult?> CaptureAsync(AvaloniaRegionCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            AvaloniaBootstrapper.EnsureInitialized();
        }
        catch (Exception ex)
        {
            request.Screenshot.Dispose();
            request.CursorBitmap?.Dispose();
            return Task.FromException<AvaloniaRegionCaptureResult?>(ex);
        }

        TaskCompletionSource<AvaloniaRegionCaptureResult?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        async void Run()
        {
            RegionCaptureWindow? window = null;
            bool requestAttached = false;

            try
            {
                if (IsCancelRequested)
                {
                    // Escape was pressed while the screenshot was taken: never show the capture.
                    _prewarmedWindow?.Conceal();
                    request.Screenshot.Dispose();
                    request.CursorBitmap?.Dispose();
                    completion.TrySetResult(null);
                    return;
                }

                ImageEditorIntegration.Initialize();
                window = TakePrewarmedWindow();
                PrecapturedWindowList.Configure(request.RegionCaptureOptions.DetectWindows, request.RegionCaptureOptions.DetectControls);
                _activeCaptureWindow = window;

                if (window != null)
                {
                    Task<AvaloniaRegionCaptureResult?> capture = window.CaptureAsync(request);
                    requestAttached = true;

                    if (IsCancelRequested)
                    {
                        window.CancelFromEscape();
                    }

                    completion.TrySetResult(await capture);
                }
                else
                {
                    window = new RegionCaptureWindow(request);
                    _activeCaptureWindow = window;
                    requestAttached = true;
                    Task<AvaloniaRegionCaptureResult?> capture = window.CaptureAsync();

                    if (IsCancelRequested)
                    {
                        window.CancelFromEscape();
                    }

                    completion.TrySetResult(await capture);
                }
            }
            catch (Exception ex)
            {
                if (window == null)
                {
                    // An early shown prewarmed overlay must not stay on screen.
                    _prewarmedWindow?.Conceal();
                }

                if (!requestAttached || window == null || !window.IsVisible)
                {
                    request.Screenshot.Dispose();
                    request.CursorBitmap?.Dispose();
                }

                completion.TrySetException(ex);
            }
            finally
            {
                // The capture flow that called this method ends the capture input (EndCaptureInput) when it is done.
                _activeCaptureWindow = null;

                // Get the next window ready once the current capture flow has settled.
                _ = Task.Delay(PrewarmDelayAfterCapture).ContinueWith(_ => PrepareWindow(), TaskScheduler.Default);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            // The hotkey message: attach the screenshot in this dispatcher job, so the next rendered frame shows the
            // whole capture (a posted job can run after a render).
            Run();
        }
        else
        {
            Dispatcher.UIThread.Post(Run);
        }

        return completion.Task;
    }

    // Short: a capture started soon after the last one (Escape, then the hotkey again) still finds a prewarmed window.
    private static readonly TimeSpan PrewarmDelayAfterCapture = TimeSpan.FromMilliseconds(100);
    private static RegionCaptureWindow? _activeCaptureWindow;
    private static bool _escapeGuardHooked;
    private static bool _captureInProgress;

    /// <summary>
    /// A region capture runs, from <see cref="BeginCaptureInput"/> to <see cref="EndCaptureInput"/> (UI thread). Other
    /// captures must not start meanwhile: their screenshot would contain the capture overlay.
    /// </summary>
    public static bool IsCaptureInProgress => _captureInProgress;

    /// <summary>
    /// True when Escape was pressed since the capture hotkey (or <see cref="BeginCaptureInput"/>). Set by the keyboard
    /// guard thread the moment the key is pressed, so every step of the capture can check it.
    /// </summary>
    public static bool IsCancelRequested => RegionCaptureKeyboardGuard.CancelRequested;

    /// <summary>
    /// Call on the UI thread when a region capture starts, and always pair it with <see cref="EndCaptureInput"/> when the
    /// capture flow is done. Marks the capture in progress and confirms the Escape guard the capture hotkey armed
    /// (keeping an Escape already pressed), or arms it for captures started without the keyboard. Calling it again
    /// during the same capture keeps the state. See RegionCaptureKeyboardGuard.
    /// </summary>
    public static void BeginCaptureInput()
    {
        if (!_escapeGuardHooked)
        {
            RegionCaptureKeyboardGuard.EscapePressed += OnGuardedEscape;
            _escapeGuardHooked = true;
        }

        _captureInProgress = true;
        RegionCaptureKeyboardGuard.BeginCapture();
    }

    /// <summary>The capture flow ended (UI thread): Escape is no longer consumed and another capture can start.</summary>
    public static void EndCaptureInput()
    {
        RegionCaptureKeyboardGuard.EndCapture();
        _captureInProgress = false;
    }

    private static void OnGuardedEscape()
    {
        if (_activeCaptureWindow != null)
        {
            // Every Escape of the capture arrives here, whichever window has the keyboard.
            _activeCaptureWindow.HandleGuardedEscape();
        }
        else
        {
            // Not attached yet: hide the overlay now; the capture is dropped before it is attached.
            _prewarmedWindow?.Conceal();
        }
    }
    private static RegionCaptureWindow? _prewarmedWindow;
    private static bool _displaySettingsHooked;

    /// <summary>
    /// Creates a cloaked region capture window in the background so the next capture only attaches the screenshot and
    /// uncloaks it (no window creation, XAML loading, layout or Show() on the hotkey path).
    /// </summary>
    public static void PrepareWindow(Func<RegionCapturePrewarmOptions?>? optionsProvider)
    {
        _prewarmOptionsProvider = optionsProvider;
        PrepareWindow();
    }

    private static Func<RegionCapturePrewarmOptions?>? _prewarmOptionsProvider;

    private static void PrepareWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_prewarmedWindow is { IsPrewarmedAndIdle: true })
            {
                return;
            }

            try
            {
                if (!_displaySettingsHooked)
                {
                    // Rebuild the window for the new virtual screen when monitors change.
                    SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        RegionCaptureWindow? stale = _prewarmedWindow;
                        _prewarmedWindow = null;
                        stale?.Close();
                        PrepareWindow();
                    });

                    _displaySettingsHooked = true;
                }

                ImageEditorIntegration.Initialize();
                RegionCapturePrewarmOptions? options = _prewarmOptionsProvider?.Invoke();

                if (options != null)
                {
                    // What the precapture lists with its frame (see PrecapturedWindowList).
                    PrecapturedWindowList.Configure(options.RegionCaptureOptions.DetectWindows, options.RegionCaptureOptions.DetectControls);
                }

                _prewarmedWindow = RegionCaptureWindow.CreatePrewarmed(CaptureHelpers.GetScreenBounds(), options);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Region capture window prewarm failed.");
                _prewarmedWindow = null;
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Hides the prewarmed window again when a capture was aborted before the region capture started.</summary>
    public static void ConcealPrewarmedWindow()
    {
        Dispatcher.UIThread.Post(() => _prewarmedWindow?.Conceal());
    }

    private static RegionCaptureWindow? TakePrewarmedWindow()
    {
        RegionCaptureWindow? window = _prewarmedWindow;
        _prewarmedWindow = null;

        if (window is { IsPrewarmedAndIdle: true })
        {
            return window;
        }

        window?.Close();
        return null;
    }

    /// <summary>
    /// Builds a region capture window once without showing it, so the first capture after startup does not pay
    /// for JIT, XAML loading, styles, templates, icons and font enumeration. Must run on the UI thread.
    /// Uses default options so the user's settings are never touched.
    /// </summary>
    public static void WarmUp()
    {
        RegionCaptureWindow? window = null;

        try
        {
            AvaloniaBootstrapper.EnsureInitialized();
            ImageEditorIntegration.Initialize();

            const int size = 64;
            SKBitmap screenshot = new SKBitmap(size, size);

            try
            {
                window = new RegionCaptureWindow(new AvaloniaRegionCaptureRequest
                {
                    Screenshot = screenshot,
                    ScreenBounds = new Rectangle(0, 0, size, size),
                    RegionCaptureOptions = new RegionCaptureOptions(),
                    ImageEditorOptions = new ImageEditorOptions()
                });
            }
            catch
            {
                screenshot.Dispose();
                throw;
            }

            window.WarmUpHidden();
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e, "Region capture warm-up failed.");
        }
        finally
        {
            try
            {
                // Closing a window that was never shown releases its native handle and runs OnClosed,
                // which disposes the workspace, the screenshot and the view model.
                window?.Close();
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Region capture warm-up cleanup failed.");
            }
        }
    }

    /// <summary>
    /// Lets the user draw a rectangle over the live desktop and returns it in screen pixels, or null when cancelled.
    /// The selection window is closed when the task completes, so the caller can capture the screen right after.
    /// </summary>
    public static Task<Rectangle?> SelectTransparentRegionAsync(Rectangle screenBounds, bool lockCursorToWindow)
    {
        AvaloniaBootstrapper.EnsureInitialized();

        TaskCompletionSource<Rectangle?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                RegionCaptureTransparentWindow window = new(screenBounds, lockCursorToWindow);
                Rectangle? selection = await window.SelectAsync();

                if (selection is Rectangle rectangle)
                {
                    SetLastRegion(rectangle, [rectangle]);
                }

                completion.TrySetResult(selection);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }
}
