using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarMonitor
{
    [ComVisible(true)]
    [Guid("13790826-15fa-46d0-9814-c2a5c6c11f32")]
    [CSDeskBand.CSDeskBandRegistration(Name = "taskbar-monitor", ShowDeskBand = true)]
    public class Deskband : CSDeskBand.CSDeskBandWin
    {
        private static Control _control;
        public Size Size { get { return Options.MinHorizontalSize; } }

        public Deskband()
        {
            try
            {
                Application.EnableVisualStyles();
                AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
                Options opt = TaskbarMonitor.Options.ReadFromDisk();
                TaskbarInfo.TaskbarSizeChanged += TaskbarInfo_TaskbarSizeChanged;
                TaskbarInfo.TaskbarEdgeChanged += TaskbarInfo_TaskbarEdgeChanged;
                TaskbarInfo.TaskbarOrientationChanged += TaskbarInfo_TaskbarOrientationChanged;
                Monitor monitor = Monitor.GetInstance(opt);
                var ctl = new SystemWatcherControl(monitor, false, this);
                _control = ctl;

                ctl.OnChangeSize += Ctl_OnChangeSize;
                //Options.HeightCanChange = false;                
                Ctl_OnChangeSize(ctl.Size);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error intializing Deskband: {ex.Message}", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static void OnProcessExit(object sender, EventArgs e)
        {
            // remove other monitors
        }

        private void TaskbarInfo_TaskbarOrientationChanged(object sender, CSDeskBand.TaskbarOrientationChangedEventArgs e)
        {
            // Vertical taskbars use different metrics.
            RefreshControlMetrics();
        }

        private void TaskbarInfo_TaskbarEdgeChanged(object sender, CSDeskBand.TaskbarEdgeChangedEventArgs e)
        {
            // The band may have been moved to another monitor.
            RefreshControlMetrics();
        }

        private void TaskbarInfo_TaskbarSizeChanged(object sender, CSDeskBand.TaskbarSizeChangedEventArgs e)
        {
            // Taskbar thickness changes with the monitor DPI / taskbar size.
            RefreshControlMetrics();
        }

        private void RefreshControlMetrics()
        {
            var ctl = _control as SystemWatcherControl;
            if (ctl == null || ctl.IsDisposed || ctl.Disposing)
                return;

            try
            {
                ctl.RefreshDisplayMetrics();
            }
            catch
            {
                // Never let a display change take down Explorer.
            }
        }

        private void Ctl_OnChangeSize(Size size)
        {
            // The control computes its size from the taskbar thickness and DPI.
            // Reflect the (scaled) width in the deskband constraints so Explorer
            // gives the band enough room on high-DPI / high-resolution displays.
            int height = Math.Max(1, size.Height);
            Options.MinHorizontalSize = new Size(size.Width, height);
            // Options.MaxHorizontalHeight = size.Height;
        }

        protected override Control Control => _control;         
    }
}
