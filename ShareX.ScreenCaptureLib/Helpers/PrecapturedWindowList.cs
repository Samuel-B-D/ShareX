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

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// The windows (and controls) on screen when a precapture was taken, listed on the precapture thread right after the
    /// frame (see DesktopDuplicationCapture). The region capture overlay then highlights the window under the pointer in
    /// its first frame, matching the screenshot, instead of a few frames later once its own enumeration is done.
    /// </summary>
    internal static class PrecapturedWindowList
    {
        private static readonly long MaxAge = Stopwatch.Frequency * 3;

        private sealed record Snapshot(Task<IReadOnlyList<SimpleWindowInfo>> Windows, bool IncludeChildWindows, long Timestamp);

        private static Snapshot? current;

        // 0: no window detection, 1: windows, 2: windows and controls. Set from the options of the next region capture.
        private static volatile int mode;

        /// <summary>What the next region capture detects (from the options its prewarmed window was built with).</summary>
        public static void Configure(bool detectWindows, bool detectControls)
        {
            mode = !detectWindows ? 0 : detectControls ? 2 : 1;
        }

        /// <summary>Precapture thread, right after a frame was taken: lists the windows of that moment.</summary>
        public static void Prefetch()
        {
            int detection = mode;

            if (detection == 0)
            {
                Volatile.Write(ref current, null);
                return;
            }

            TaskCompletionSource<IReadOnlyList<SimpleWindowInfo>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref current, new Snapshot(completion.Task, detection == 2, Stopwatch.GetTimestamp()));

            try
            {
                // The overlay is still cloaked, so it is not in the list (cloaked windows are skipped).
                WindowsRectangleList windows = new WindowsRectangleList
                {
                    IncludeChildWindows = detection == 2,
                    Timeout = 5000
                };

                completion.TrySetResult(windows.GetWindowInfoList());
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Precaptured window list failed.");
                completion.TrySetException(e);
            }
        }

        /// <summary>
        /// The list of the last precapture (possibly still being made), when it is recent and made with the same detection
        /// options; null otherwise. It can only be taken once.
        /// </summary>
        public static Task<IReadOnlyList<SimpleWindowInfo>>? Take(bool includeChildWindows)
        {
            Snapshot? snapshot = Interlocked.Exchange(ref current, null);

            if (snapshot == null || snapshot.IncludeChildWindows != includeChildWindows ||
                Stopwatch.GetTimestamp() - snapshot.Timestamp > MaxAge)
            {
                return null;
            }

            return snapshot.Windows;
        }

        public static void Discard()
        {
            Volatile.Write(ref current, null);
        }
    }
}
