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

using Microsoft.Win32;
using ShareX.HelpersLib;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Captures the composed desktop with DXGI Desktop Duplication. The Direct3D devices and staging textures are
    /// created once (see <see cref="Warmup"/>), so a capture only creates the duplications (well under 1 ms), acquires
    /// the next composed frame and reads it back: a few milliseconds plus at most one display refresh, instead of
    /// 30-90 ms for a GDI BitBlt with CAPTUREBLT and its bitmap conversions.
    /// <para>
    /// A new duplication only delivers a usable image once DWM composes a frame. On a static screen nothing is
    /// composed, so a composition is triggered with <see cref="CompositionTrigger"/>, which changes nothing other
    /// applications can see or react to. The optional <c>beforeAcquire</c> callback runs after the duplications exist;
    /// anything it changes on screen can be in the frame, so it must not show windows over other applications.
    /// </para>
    /// <para>
    /// <see cref="Precapture"/> takes a frame of every output ahead of time (when the modifier of a capture hotkey is
    /// pressed, before the foreground application sees the key: Alt closes menus and popups) and keeps it in the staging
    /// textures. A capture that is allowed to use it and finds it claimed (<see cref="ClaimPrecapture"/>) and recent
    /// returns that frame instead of a new one.
    /// </para>
    /// Returns null when the duplication cannot be used (rotated or HDR outputs, secure desktop, device lost, no frame
    /// in time, ...) so the caller falls back to GDI.
    /// </summary>
    internal static class DesktopDuplicationCapture
    {
        private const int FrameWaitMilliseconds = 100;
        private const int PrecaptureNone = 0, PrecaptureTaken = 1, PrecaptureClaimed = 2;
        private static readonly long PrecaptureMaxAge = Stopwatch.Frequency * 3;

        private static readonly object sync = new object();

        // PrecaptureTaken or PrecaptureClaimed: the staging texture of each output marked Precaptured holds the whole
        // output as it was at precaptureTimestamp. Changed without sync, so the hook thread never waits for it.
        private static int precaptureState;
        private static long precaptureTimestamp;
        private static List<AdapterState> adapters;
        private static bool displaySettingsHooked;

        private sealed class AdapterState : IDisposable
        {
            public ID3D11Device Device;
            public ID3D11DeviceContext Context;
            public readonly List<OutputState> Outputs = new List<OutputState>();

            public void Dispose()
            {
                foreach (OutputState output in Outputs) output.Dispose();
                Context?.Dispose();
                Device?.Dispose();
            }
        }

        private sealed class OutputState : IDisposable
        {
            public IDXGIOutput1 Output;
            public Rectangle Bounds;
            public ModeRotation Rotation;
            public ID3D11Texture2D Staging;
            public bool Precaptured;

            public void Dispose()
            {
                Staging?.Dispose();
                Output?.Dispose();
            }
        }

        private sealed class OutputCapture : IDisposable
        {
            public AdapterState Adapter;
            public OutputState Output;
            public Rectangle Intersection;
            public IDXGIOutputDuplication Duplication;

            public void Dispose()
            {
                Duplication?.Dispose();
            }
        }

        /// <summary>Creates the Direct3D devices in the background so the first capture does not pay for it.</summary>
        public static void Warmup()
        {
            Task.Run(() =>
            {
                try
                {
                    lock (sync)
                    {
                        EnsureInitialized();
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Desktop duplication warmup failed.");
                }
            });
        }

        /// <summary>
        /// Captures <paramref name="rectangle"/> (screen pixels). With <paramref name="usePrecapture"/>, a claimed and
        /// recent frame taken by <see cref="Precapture"/> is returned instead of a new one. Any capture drops the
        /// precaptured frame.
        /// </summary>
        public static SKBitmap TryCapture(Rectangle rectangle, Action beforeAcquire = null, bool usePrecapture = false)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                return null;
            }

            lock (sync)
            {
                List<OutputCapture> captures = new List<OutputCapture>();
                SKBitmap bitmap = null;
                int precapture = Interlocked.Exchange(ref precaptureState, PrecaptureNone);

                try
                {
                    EnsureInitialized();

                    long covered = 0;

                    foreach (AdapterState adapter in adapters)
                    {
                        foreach (OutputState output in adapter.Outputs)
                        {
                            Rectangle intersection = Rectangle.Intersect(output.Bounds, rectangle);

                            if (intersection.Width <= 0 || intersection.Height <= 0)
                            {
                                continue;
                            }

                            if (output.Rotation != ModeRotation.Identity && output.Rotation != ModeRotation.Unspecified)
                            {
                                return null;
                            }

                            captures.Add(new OutputCapture { Adapter = adapter, Output = output, Intersection = intersection });
                            covered += (long)intersection.Width * intersection.Height;
                        }
                    }

                    if (captures.Count == 0)
                    {
                        return null;
                    }

                    if (usePrecapture && precapture == PrecaptureClaimed &&
                        Stopwatch.GetTimestamp() - precaptureTimestamp <= PrecaptureMaxAge &&
                        captures.TrueForAll(x => x.Output.Precaptured))
                    {
                        bitmap = CreateBitmap(rectangle, covered);

                        foreach (OutputCapture capture in captures)
                        {
                            CopyFromStaging(capture, rectangle, bitmap);
                        }

                        DebugHelper.WriteLine("Screen capture: used the frame taken when the hotkey modifier was pressed.");
                        return bitmap;
                    }

                    // The staging textures are written below: the precaptured frame is gone.
                    ClearPrecapturedOutputs();

                    // Duplicate every output first: only frames composed after this point are delivered.
                    foreach (OutputCapture capture in captures)
                    {
                        capture.Duplication = capture.Output.Output.DuplicateOutput(capture.Adapter.Device);
                    }

                    // Make DWM compose each monitor now, without any visible change, so the frame is the desktop as it
                    // is at this moment (even on a static screen) and nothing on screen reacts before it is taken.
                    List<Rectangle> monitors = new List<Rectangle>(captures.Count);
                    foreach (OutputCapture capture in captures) monitors.Add(capture.Output.Bounds);
                    CompositionTrigger.Trigger(monitors);

                    try
                    {
                        beforeAcquire?.Invoke();
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e, "Desktop duplication pre-acquire callback failed.");
                    }

                    bitmap = CreateBitmap(rectangle, covered);

                    long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * FrameWaitMilliseconds / 1000;

                    foreach (OutputCapture capture in captures)
                    {
                        if (!AcquireToStaging(capture, deadline))
                        {
                            bitmap.Dispose();
                            return null;
                        }

                        CopyFromStaging(capture, rectangle, bitmap);
                    }

                    return bitmap;
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Desktop duplication capture failed, using GDI.");
                    bitmap?.Dispose();
                    Reset();
                    return null;
                }
                finally
                {
                    foreach (OutputCapture capture in captures) capture.Dispose();
                }
            }
        }

        /// <summary>
        /// Takes a frame of every output into the staging textures, waiting at most <paramref name="timeoutMilliseconds"/>
        /// for DWM to compose it, for a capture that starts right after (see <see cref="ClaimPrecapture"/>). Called by the
        /// keyboard hook before the key reaches the foreground application. Never waits for another capture or creates the
        /// Direct3D devices (see <see cref="Warmup"/>): it gives up instead.
        /// </summary>
        internal static bool Precapture(int timeoutMilliseconds)
        {
            if (!Monitor.TryEnter(sync))
            {
                return false;
            }

            List<OutputCapture> captures = new List<OutputCapture>();

            try
            {
                Interlocked.Exchange(ref precaptureState, PrecaptureNone);
                ClearPrecapturedOutputs();

                if (adapters == null)
                {
                    return false;
                }

                foreach (AdapterState adapter in adapters)
                {
                    foreach (OutputState output in adapter.Outputs)
                    {
                        if (output.Rotation == ModeRotation.Identity || output.Rotation == ModeRotation.Unspecified)
                        {
                            captures.Add(new OutputCapture { Adapter = adapter, Output = output, Intersection = output.Bounds });
                        }
                    }
                }

                if (captures.Count == 0)
                {
                    return false;
                }

                foreach (OutputCapture capture in captures)
                {
                    capture.Duplication = capture.Output.Output.DuplicateOutput(capture.Adapter.Device);
                }

                List<Rectangle> monitors = new List<Rectangle>(captures.Count);
                foreach (OutputCapture capture in captures) monitors.Add(capture.Output.Bounds);
                CompositionTrigger.Trigger(monitors);

                long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * timeoutMilliseconds / 1000;

                foreach (OutputCapture capture in captures)
                {
                    if (!AcquireToStaging(capture, deadline))
                    {
                        ClearPrecapturedOutputs();
                        return false;
                    }

                    capture.Output.Precaptured = true;
                }

                precaptureTimestamp = Stopwatch.GetTimestamp();
                Volatile.Write(ref precaptureState, PrecaptureTaken);
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Desktop duplication precapture failed.");
                ClearPrecapturedOutputs();
                Reset();
                return false;
            }
            finally
            {
                foreach (OutputCapture capture in captures) capture.Dispose();
                Monitor.Exit(sync);
            }
        }

        /// <summary>The capture hotkey was pressed: the precaptured frame, if any, is for the capture it starts.</summary>
        internal static void ClaimPrecapture()
        {
            Interlocked.CompareExchange(ref precaptureState, PrecaptureClaimed, PrecaptureTaken);
        }

        /// <summary>Something happened after the precapture that was not the capture hotkey (another key, modifier released).</summary>
        internal static void DiscardUnclaimedPrecapture()
        {
            Interlocked.CompareExchange(ref precaptureState, PrecaptureNone, PrecaptureTaken);
        }

        /// <summary>No capture will use the precaptured frame.</summary>
        internal static void DiscardPrecapture()
        {
            Interlocked.Exchange(ref precaptureState, PrecaptureNone);
        }

        // Under sync.
        private static void ClearPrecapturedOutputs()
        {
            if (adapters == null)
            {
                return;
            }

            foreach (AdapterState adapter in adapters)
            {
                foreach (OutputState output in adapter.Outputs) output.Precaptured = false;
            }
        }

        private static SKBitmap CreateBitmap(Rectangle rectangle, long covered)
        {
            // Opaque: every pixel gets alpha 255, which lets Skia copy the screenshot instead of blending it.
            SKBitmap bitmap = new SKBitmap(new SKImageInfo(rectangle.Width, rectangle.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));

            if (covered < (long)rectangle.Width * rectangle.Height)
            {
                // Parts of the rectangle are outside every monitor (gaps in the virtual screen); GDI returns black.
                bitmap.Erase(SKColors.Black);
            }

            return bitmap;
        }

        /// <summary>
        /// Acquires the next composed frame of the output and copies its <see cref="OutputCapture.Intersection"/> into the
        /// staging texture, at the same position.
        /// </summary>
        private static unsafe bool AcquireToStaging(OutputCapture capture, long deadline)
        {
            AdapterState adapter = capture.Adapter;
            OutputState output = capture.Output;
            Rectangle intersection = capture.Intersection;
            int sourceX = intersection.X - output.Bounds.X;
            int sourceY = intersection.Y - output.Bounds.Y;

            while (true)
            {
                int remaining = (int)Math.Max(0, (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);

                if (capture.Duplication.AcquireNextFrame((uint)remaining, out OutduplFrameInfo frameInfo, out IDXGIResource resource).Failure)
                {
                    // No frame was composed in time.
                    return false;
                }

                try
                {
                    using ID3D11Texture2D texture = resource.QueryInterface<ID3D11Texture2D>();
                    Texture2DDescription description = texture.Description;

                    if (description.Format != Format.B8G8R8A8_UNorm)
                    {
                        return false;
                    }

                    EnsureStaging(adapter, output, description);
                    Vortice.Mathematics.Box box = new Vortice.Mathematics.Box(sourceX, sourceY, 0, sourceX + intersection.Width, sourceY + intersection.Height, 1);
                    adapter.Context.CopySubresourceRegion(output.Staging, 0, (uint)sourceX, (uint)sourceY, 0, texture, 0, box);
                }
                finally
                {
                    resource.Dispose();
                    capture.Duplication.ReleaseFrame();
                }

                if (frameInfo.LastPresentTime != 0)
                {
                    return true;
                }

                MappedSubresource mapped = adapter.Context.Map(output.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                bool empty;

                try
                {
                    // A frame without a present (LastPresentTime 0) can be an empty surface when DWM has not composed
                    // since the duplication was created. Wait for the next composed frame instead.
                    byte* source = (byte*)mapped.DataPointer + (long)sourceY * mapped.RowPitch + sourceX * 4L;
                    empty = IsEmptySurface(source, mapped.RowPitch, intersection.Width, intersection.Height);
                }
                finally
                {
                    adapter.Context.Unmap(output.Staging, 0);
                }

                if (!empty)
                {
                    return true;
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    return false;
                }
            }
        }

        /// <summary>Copies the part of <paramref name="rectangle"/> on the output from its staging texture into <paramref name="bitmap"/>.</summary>
        private static unsafe void CopyFromStaging(OutputCapture capture, Rectangle rectangle, SKBitmap bitmap)
        {
            AdapterState adapter = capture.Adapter;
            OutputState output = capture.Output;
            Rectangle intersection = capture.Intersection;
            int sourceX = intersection.X - output.Bounds.X;
            int sourceY = intersection.Y - output.Bounds.Y;

            MappedSubresource mapped = adapter.Context.Map(output.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                byte* source = (byte*)mapped.DataPointer + (long)sourceY * mapped.RowPitch + sourceX * 4L;
                int destinationX = intersection.X - rectangle.X;
                int destinationY = intersection.Y - rectangle.Y;
                byte* destination = (byte*)bitmap.GetPixels() + (long)destinationY * bitmap.RowBytes + destinationX * 4L;

                for (int y = 0; y < intersection.Height; y++)
                {
                    CopyRowOpaque(new ReadOnlySpan<uint>(source, intersection.Width), new Span<uint>(destination, intersection.Width));
                    source += mapped.RowPitch;
                    destination += bitmap.RowBytes;
                }
            }
            finally
            {
                adapter.Context.Unmap(output.Staging, 0);
            }
        }

        private static void EnsureStaging(AdapterState adapter, OutputState output, Texture2DDescription description)
        {
            if (output.Staging != null && output.Staging.Description.Width == description.Width &&
                output.Staging.Description.Height == description.Height)
            {
                return;
            }

            output.Staging?.Dispose();
            output.Staging = adapter.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = description.Width,
                Height = description.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
        }

        private static unsafe bool IsEmptySurface(byte* pixels, uint rowPitch, int width, int height)
        {
            int stepX = Math.Max(1, width / 64);
            int stepY = Math.Max(1, height / 64);

            for (int y = 0; y < height; y += stepY)
            {
                uint* row = (uint*)(pixels + (long)y * rowPitch);

                for (int x = 0; x < width; x += stepX)
                {
                    if (row[x] != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Copies BGRA pixels and forces alpha to 255 (duplicated desktop alpha is undefined), like the GDI path.</summary>
        private static void CopyRowOpaque(ReadOnlySpan<uint> source, Span<uint> destination)
        {
            int i = 0;

            if (Vector.IsHardwareAccelerated)
            {
                Vector<uint> alpha = new Vector<uint>(0xFF000000);
                ReadOnlySpan<Vector<uint>> sourceVectors = MemoryMarshal.Cast<uint, Vector<uint>>(source);
                Span<Vector<uint>> destinationVectors = MemoryMarshal.Cast<uint, Vector<uint>>(destination);

                for (int v = 0; v < sourceVectors.Length; v++)
                {
                    destinationVectors[v] = sourceVectors[v] | alpha;
                }

                i = sourceVectors.Length * Vector<uint>.Count;
            }

            for (; i < source.Length; i++)
            {
                destination[i] = source[i] | 0xFF000000;
            }
        }

        private static void EnsureInitialized()
        {
            if (adapters != null)
            {
                return;
            }

            if (!displaySettingsHooked)
            {
                SystemEvents.DisplaySettingsChanged += (_, _) =>
                {
                    lock (sync)
                    {
                        Reset();
                    }
                };

                displaySettingsHooked = true;
            }

            List<AdapterState> result = new List<AdapterState>();

            try
            {
                using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

                for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 dxgiAdapter).Success; adapterIndex++)
                {
                    using (dxgiAdapter)
                    {
                        AdapterState adapter = null;

                        for (uint outputIndex = 0; dxgiAdapter.EnumOutputs(outputIndex, out IDXGIOutput dxgiOutput).Success; outputIndex++)
                        {
                            using (dxgiOutput)
                            {
                                OutputDescription description = dxgiOutput.Description;

                                if (!description.AttachedToDesktop)
                                {
                                    continue;
                                }

                                if (adapter == null)
                                {
                                    if (D3D11.D3D11CreateDevice(dxgiAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).Failure)
                                    {
                                        break;
                                    }

                                    adapter = new AdapterState { Device = device, Context = context };
                                    result.Add(adapter);
                                }

                                var coordinates = description.DesktopCoordinates;
                                adapter.Outputs.Add(new OutputState
                                {
                                    Output = dxgiOutput.QueryInterface<IDXGIOutput1>(),
                                    Bounds = Rectangle.FromLTRB(coordinates.Left, coordinates.Top, coordinates.Right, coordinates.Bottom),
                                    Rotation = description.Rotation
                                });
                            }
                        }
                    }
                }
            }
            catch
            {
                foreach (AdapterState adapter in result) adapter.Dispose();
                throw;
            }

            adapters = result;
        }

        private static void Reset()
        {
            Interlocked.Exchange(ref precaptureState, PrecaptureNone);

            if (adapters != null)
            {
                foreach (AdapterState adapter in adapters) adapter.Dispose();
                adapters = null;
            }
        }
    }
}
