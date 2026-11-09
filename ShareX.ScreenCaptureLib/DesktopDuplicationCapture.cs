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
    /// composed, so the caller can trigger a composition that does not change any pixel (showing a fully transparent
    /// window) through the <c>beforeAcquire</c> callback, which runs after the duplications exist.
    /// </para>
    /// Returns null when the duplication cannot be used (rotated or HDR outputs, secure desktop, device lost, no frame
    /// in time, ...) so the caller falls back to GDI.
    /// </summary>
    internal static class DesktopDuplicationCapture
    {
        private const int FrameWaitMilliseconds = 100;

        private static readonly object sync = new object();
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

        public static SKBitmap TryCapture(Rectangle rectangle, Action beforeAcquire = null)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                return null;
            }

            lock (sync)
            {
                List<OutputCapture> captures = new List<OutputCapture>();
                SKBitmap bitmap = null;

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

                    // Duplicate every output first: only frames composed after this point are delivered.
                    foreach (OutputCapture capture in captures)
                    {
                        capture.Duplication = capture.Output.Output.DuplicateOutput(capture.Adapter.Device);
                    }

                    try
                    {
                        beforeAcquire?.Invoke();
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e, "Desktop duplication pre-acquire callback failed.");
                    }

                    // Opaque: every pixel gets alpha 255, which lets Skia copy the screenshot instead of blending it.
                    bitmap = new SKBitmap(new SKImageInfo(rectangle.Width, rectangle.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));

                    if (covered < (long)rectangle.Width * rectangle.Height)
                    {
                        // Parts of the rectangle are outside every monitor (gaps in the virtual screen); GDI returns black.
                        bitmap.Erase(SKColors.Black);
                    }

                    long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * FrameWaitMilliseconds / 1000;

                    foreach (OutputCapture capture in captures)
                    {
                        if (!CaptureOutput(capture, rectangle, bitmap, deadline))
                        {
                            bitmap.Dispose();
                            return null;
                        }
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

        private static unsafe bool CaptureOutput(OutputCapture capture, Rectangle rectangle, SKBitmap bitmap, long deadline)
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

                MappedSubresource mapped = adapter.Context.Map(output.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    byte* source = (byte*)mapped.DataPointer + (long)sourceY * mapped.RowPitch + sourceX * 4L;

                    // A frame without a present (LastPresentTime 0) can be an empty surface when DWM has not composed since
                    // the duplication was created. Wait for the next composed frame instead.
                    if (frameInfo.LastPresentTime == 0 && IsEmptySurface(source, mapped.RowPitch, intersection.Width, intersection.Height))
                    {
                        if (Stopwatch.GetTimestamp() >= deadline)
                        {
                            return false;
                        }

                        continue;
                    }

                    int destinationX = intersection.X - rectangle.X;
                    int destinationY = intersection.Y - rectangle.Y;
                    byte* destination = (byte*)bitmap.GetPixels() + (long)destinationY * bitmap.RowBytes + destinationX * 4L;

                    for (int y = 0; y < intersection.Height; y++)
                    {
                        CopyRowOpaque(new ReadOnlySpan<uint>(source, intersection.Width), new Span<uint>(destination, intersection.Width));
                        source += mapped.RowPitch;
                        destination += bitmap.RowBytes;
                    }

                    return true;
                }
                finally
                {
                    adapter.Context.Unmap(output.Staging, 0);
                }
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
            if (adapters != null)
            {
                foreach (AdapterState adapter in adapters) adapter.Dispose();
                adapters = null;
            }
        }
    }
}
