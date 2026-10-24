using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarMonitor.BLL
{
    /// <summary>
    /// Helpers to obtain the DPI scale of the monitor that owns a taskbar.
    /// taskbar-monitor draws everything in device pixels, so every UI metric
    /// has to be multiplied by the scale of the monitor the taskbar lives on.
    /// </summary>
    public static class DisplayInfo
    {
        public const float BaseDpi = 96f;

        private const int MDT_EFFECTIVE_DPI = 0;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>
        /// Scale of the system DPI (the value used when no per-monitor
        /// information is available).
        /// </summary>
        public static float SystemScale
        {
            get
            {
                try
                {
                    using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                        return NormalizeScale(graphics.DpiX / BaseDpi);
                }
                catch
                {
                    return 1f;
                }
            }
        }

        /// <summary>
        /// DPI scale (1.0 == 96 DPI) of the monitor that contains the window.
        /// </summary>
        public static float GetScaleForWindow(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                // GetDpiForWindow (Windows 10 1607+) is the API recommended for
                // per-monitor DPI aware processes; the taskbar handles belong to
                // Explorer, which is per-monitor aware on Windows 10/11.
                try
                {
                    uint dpi = GetDpiForWindow(handle);
                    if (dpi > 0)
                        return NormalizeScale(dpi / BaseDpi);
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }

                // Fallback for older systems. Note that GetDpiForMonitor only
                // reports the real monitor DPI when the calling process is
                // per-monitor DPI aware; for a system DPI aware process it
                // returns the system DPI (which is what we want as a fallback).
                try
                {
                    IntPtr monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
                    uint dpiX, dpiY;
                    if (monitor != IntPtr.Zero &&
                        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0 &&
                        dpiX > 0)
                    {
                        return NormalizeScale(dpiX / BaseDpi);
                    }
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }
            }

            return SystemScale;
        }

        /// <summary>
        /// DPI scale of the monitor described by <paramref name="screen"/>.
        /// </summary>
        public static float GetScaleForScreen(Screen screen)
        {
            if (screen != null)
            {
                try
                {
                    Rectangle bounds = screen.Bounds;
                    var point = new POINT(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
                    uint dpiX, dpiY;
                    if (monitor != IntPtr.Zero &&
                        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0 &&
                        dpiX > 0)
                    {
                        return NormalizeScale(dpiX / BaseDpi);
                    }
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }
            }

            return SystemScale;
        }

        /// <summary>
        /// DPI scale for a control. Uses the control's handle when available,
        /// otherwise the monitor the control is shown on.
        /// </summary>
        public static float GetScaleForControl(Control control)
        {
            if (control != null)
            {
                if (control.IsHandleCreated)
                    return GetScaleForWindow(control.Handle);

                try
                {
                    return GetScaleForScreen(Screen.FromControl(control));
                }
                catch { }
            }

            return SystemScale;
        }

        /// <summary>
        /// Scale derived from the physical resolution of the screen.
        /// A 1920x1080 display is the reference (1.0); larger displays get
        /// proportionally larger graphs even when Windows is configured at 100%.
        /// </summary>
        public static float GetResolutionScale(Screen screen)
        {
            if (screen == null || screen.Bounds.Width <= 0 || screen.Bounds.Height <= 0)
                return 1f;

            double referencePixels = 1920.0 * 1080.0;
            double screenPixels = (double)screen.Bounds.Width * screen.Bounds.Height;
            double ratio = screenPixels / referencePixels;

            if (ratio <= 1.0)
                return 1f;

            return NormalizeScale((float)Math.Sqrt(ratio));
        }

        /// <summary>
        /// Clamps the scale to sane values (avoids absurd metrics on remote,
        /// virtual or misconfigured displays).
        /// </summary>
        public static float NormalizeScale(float scale)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
                return 1f;

            return Math.Min(4f, Math.Max(0.5f, scale));
        }
    }
}
