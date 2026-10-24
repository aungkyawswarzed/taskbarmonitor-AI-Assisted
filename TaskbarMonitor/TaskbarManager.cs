using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TaskbarMonitor.BLL;

using System.Diagnostics;
using System.Drawing;

namespace TaskbarMonitor
{
    public class Taskbar
    {
        public Taskbar(bool ismain = false)
        {
            this.IsMainTaskbar = ismain;
        }
        public bool IsMainTaskbar { get; protected set; }
        public IntPtr TargetWnd = IntPtr.Zero;
        public IntPtr TrayWnd = IntPtr.Zero;
        public IntPtr ClockWnd = IntPtr.Zero;
        public SystemWatcherControl TaskbarMonitorControl;        
        public Rectangle PreviousRect = Rectangle.Empty;
    }
    public class TaskbarManager: IDisposable
    {
        private const int intervalToMonitorTaskbars = 4000;

        public Monitor Monitor { get; private set; }                

        List<Taskbar> TaskbarList = new List<Taskbar>();
        Taskbar MainTaskbar = null;        

        List<IntPtr> g_hook = new List<IntPtr>();
        List<GCHandle> gchs = new List<GCHandle>();

        private System.Windows.Forms.Timer timer;
        private bool disposed = false;

        public SystemWatcherControl MainControl
        {
            get
            {
                Taskbar main = MainTaskbar;
                if (main == null || main.TaskbarMonitorControl == null || main.TaskbarMonitorControl.IsDisposed)
                    return null;
                return main.TaskbarMonitorControl;
            }
        }

        private TaskbarManager()
        {            
            timer = new System.Windows.Forms.Timer();
            timer.Interval = intervalToMonitorTaskbars;
            timer.Tick += Timer_Tick;

            Options opt = TaskbarMonitor.Options.ReadFromDisk();
            this.Monitor = Monitor.GetInstance(opt);
            this.Monitor.OnOptionsUpdated += Monitor_OnOptionsUpdated;                
        }

        /// <summary>
        /// Starts the retry/watch timer. This is intentionally separate from
        /// the constructor: the deskband only creates a manager to open the
        /// settings form and must not start adding its own taskbar controls.
        /// </summary>
        public void StartMonitoring()
        {
            if (disposed)
                return;
            if (!timer.Enabled)
                timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (disposed)
                return;

            // Retry even when the first attempt found no taskbar yet. The
            // timer runs on the UI thread, so no cross-thread Invoke is needed.
            AddControlsToTaskbars();
        }

        private void Monitor_OnOptionsUpdated()
        {
            for(int i = 0; i < TaskbarList.Count; i++)
            {
                var item = TaskbarList[i];
                UpdatePosition(item, true);                
                
            }
        }

        public void UpdateAllPositions()
        {
            foreach (var item in TaskbarList)
            {
                UpdatePosition(item, true);
            }            
        }

        private void UpdatePosition(Taskbar taskbar, bool force = false)
        {
            if (taskbar == null)
                return;

            var control = taskbar.TaskbarMonitorControl;
            if (control == null)
                return;

            var handle = taskbar.TargetWnd;
            Rectangle rect = BLL.Win32Api.GetWindowSize(handle);           
            Rectangle offset = Rectangle.Empty;
            if (taskbar.IsMainTaskbar)
            {
                if (taskbar.TrayWnd != IntPtr.Zero)
                {
                    offset = BLL.Win32Api.GetWindowSize(taskbar.TrayWnd);
                    // offset.Width += 20;
                }
            }
            else
            {
                if (taskbar.ClockWnd != IntPtr.Zero)
                {
                    offset = BLL.Win32Api.GetWindowSize(taskbar.ClockWnd);
                }
                else if (WindowsInformation.IsWindows11_22621())
                {
                    // The tray area scales with the DPI of the monitor, not
                    // with the graph resolution scale.
                    float scale = DisplayInfo.GetScaleForWindow(taskbar.TargetWnd);
                    offset = new Rectangle(0, 0, (int)Math.Round(100 * scale), 0);
                }
            }
              
            if (force || taskbar.PreviousRect.Width == 0 ||
                (offset.Width != taskbar.PreviousRect.Width && control.IsHandleCreated && !control.IsDisposed))
            {
                Debug.WriteLine("UpdatePosition");

                Action update = delegate
                {
                    var mopt = GetOptionsForTaskbar(taskbar);
                    control.Visible = this.Monitor.Options.EnableOnAllMonitors || mopt == null || mopt.Enabled;
                    control.Left =
                       (mopt == null || mopt.Position == MonitorOptions.DisplayPosition.RIGHT)
                       ? (rect.Width - control.Width - offset.Width)
                       : 0;

                    RECT recDiff = new RECT();
                    recDiff.left = rect.Width - control.Width - taskbar.PreviousRect.Width;
                    recDiff.top = 0;
                    recDiff.right = rect.Width - control.Width - offset.Width;
                    recDiff.bottom = rect.Bottom;

                    IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(recDiff));
                    try
                    {
                        Marshal.StructureToPtr(recDiff, ptr, true);
                        BLL.WindowList.InvalidateRect(taskbar.TargetWnd, ptr, true);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                };

                try
                {
                    if (control.InvokeRequired)
                        control.Invoke(update);
                    else
                        update();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Unable to update the taskbar control position: " + ex);
                }
            }

            taskbar.PreviousRect = offset;
        }
        
        public bool AddControlsToTaskbars()
        {
            try
            {
                return AddControlsToTaskbarsCore();
            }
            catch (Exception ex)
            {
                // A transient shell state (restart, monitor change) must not
                // take down the timer or the application.
                Debug.WriteLine("Unable to add taskbar controls: " + ex);
                return false;
            }
        }

        private bool AddControlsToTaskbarsCore()
        {
            var everythingOK = true;

            List<WindowInformation> windowListExtended = WindowList.GetAllWindowsExtendedInfo();

            // Drop controls whose taskbar window no longer exists. This covers
            // Explorer restarts where the Destroy notification was missed or
            // arrived before the new taskbar was enumerated.
            var currentTaskbarHandles = new HashSet<IntPtr>(
                windowListExtended
                    .Where(w => w.Class == "Shell_TrayWnd" || w.Class == "Shell_SecondaryTrayWnd")
                    .Select(w => w.Handle));
            foreach (var stale in TaskbarList.Where(t => !currentTaskbarHandles.Contains(t.TargetWnd)).ToList())
            {
                RemoveControl(stale);
            }

            string taskbarClass = "Shell_TrayWnd";
            string trayClass = "TrayNotifyWnd";

            var taskbarArea = windowListExtended.Where(w => w.Class == taskbarClass).SingleOrDefault();
            if (taskbarArea == null) return false;

            var trayArea = taskbarArea.ChildWindows.Where(x => x.Class == trayClass).SingleOrDefault();
            if (trayArea == null) return false;
            
            everythingOK &= AddControlToTaskbar(taskbarArea, trayArea, true);

            if (BLL.WindowsInformation.IsWindows11())
            {
                taskbarClass = "Shell_SecondaryTrayWnd";
                trayClass = "Windows.UI.Composition.DesktopWindowContentBridge";

                var taskbarsAreas = windowListExtended.Where(w => w.Class == taskbarClass).ToList();

                foreach (var tbArea in taskbarsAreas)
                {
                    var clockArea = !WindowsInformation.IsWindows11_22621() ? tbArea.ChildWindows.Where(x => x.Class == trayClass).LastOrDefault() : null;
                    everythingOK &= AddControlToTaskbar(tbArea, clockArea, false);
                }
            }

            if (everythingOK)
            {
                HookEvents();
            }
            return everythingOK;
        }

        private MonitorOptions GetOptionsForTaskbar(Taskbar tb)
        {
            MonitorOptions mopt = null;
            var monitorOptions = this.Monitor.Options.MonitorOptions;
            if (monitorOptions == null || monitorOptions.Count == 0)
                return new MonitorOptions();

            // if there is no device that match options and the system returns single setting or default settings if none exists
            if(!monitorOptions.Any(x => Screen.AllScreens.Select(y => y.DeviceName).ToList().Any(y=> y == x.Key)))
            {
                if(monitorOptions.Count == 1)
                    return monitorOptions.Values.FirstOrDefault();
                else
                    return new MonitorOptions();
            }
            var pos = BLL.Win32Api.GetWindowSize(tb.TargetWnd);
            foreach (var item in monitorOptions)
            {
                if (item.Value == null)
                    continue;

                var monitor = Screen.AllScreens.Where(x => x.DeviceName == item.Key).SingleOrDefault();
                if (monitor == null)
                    continue;
                if (pos.IntersectsWith(monitor.Bounds))                
                {                    
                    mopt = item.Value;
                }
            }
            if(mopt == null)
            {                
                return new MonitorOptions();
            }
            return mopt;            
        }

        private bool AddControlToTaskbar(WindowInformation taskbarArea, WindowInformation trayArea, bool isMainTaskbar)
        {
            Debug.WriteLine("AddControlToTaskbar");

            string expectedName = "taskbarMonitorFor" + taskbarArea.Handle;
            if (TaskbarList.Any(x => x.TaskbarMonitorControl != null &&
                                     !x.TaskbarMonitorControl.IsDisposed &&
                                     x.TaskbarMonitorControl.Name == expectedName))
                return true;

            Taskbar tb = TaskbarList.Where(x => x.TargetWnd == taskbarArea.Handle).SingleOrDefault();
            if(tb == null)
            {
                tb = new Taskbar(isMainTaskbar);
                TaskbarList.Add(tb);
                tb.TargetWnd = taskbarArea.Handle;
            }
            else if (tb.TaskbarMonitorControl != null)
            {
                // A previous attempt created a control but failed before it was
                // fully attached (for example SetParent during an Explorer
                // restart). Replace it instead of leaving a hidden/leaked one.
                try { tb.TaskbarMonitorControl.Dispose(); } catch { }
                tb.TaskbarMonitorControl = null;
            }

            if(isMainTaskbar && WindowsInformation.IsWindows11_22621())
            {
                //taskbarArea = taskbarArea.ChildWindows.Where(x => x.Class == "Windows.UI.Composition.DesktopWindowContentBridge").SingleOrDefault();
                //taskbarArea = taskbarArea.ChildWindows.Where(x => x.Class == "Windows.UI.Input.InputSite.WindowClass").SingleOrDefault();
            }
            
            
            
            var mopt = GetOptionsForTaskbar(tb);
            //if (!this.Monitor.Options.EnableOnAllMonitors || (mopt != null && !mopt.Enabled))
                //return true;


            if (isMainTaskbar)
            {
                MainTaskbar = tb;
                tb.TrayWnd = trayArea.Handle;
            }       
            else if(trayArea != null)
            {
                tb.ClockWnd = trayArea.Handle;
            }

            var rect = BLL.Win32Api.GetWindowSize(taskbarArea.Handle);
            var rectTray = trayArea != null ? BLL.Win32Api.GetWindowSize(trayArea.Handle) : Rectangle.Empty;

            var taskbarMonitorControl = new SystemWatcherControl(this.Monitor);
            tb.TaskbarMonitorControl = taskbarMonitorControl;
            taskbarMonitorControl.TaskbarWindowHandle = taskbarArea.Handle;
            // taskbarMonitorControl.Left = rect.Width - taskbarMonitorControl.Width - rectTray.Width;


            BLL.Win32Api.SetParent(taskbarMonitorControl.Handle, taskbarArea.Handle);
            // Now that the control lives on the taskbar, use that monitor's DPI
            // and taskbar thickness instead of the primary screen's values.
            taskbarMonitorControl.RefreshDisplayMetrics();
            taskbarMonitorControl.OnChangeSize += (size) => UpdatePosition(tb, true);
            //taskbarMonitorControl.BringToFront();

            if (WindowsInformation.IsWindows11_22621())
            {
                Win32Api.SetWindowLong(taskbarMonitorControl.Handle, Win32Api.GWLParameter.GWL_EXSTYLE, (uint)(0x00000000L | 0x00010000L | 0x00080000 | 0x02000000L | 0x00000020L));
                Win32Api.SetLayeredWindowAttributes(taskbarMonitorControl.Handle, 0, 255, 0x00000001 | 0x00000002);
            }
            //Win32Api.SetWindowPos(taskbarMonitorControl.Handle,  new IntPtr(-1), 0, 0, 0, 0,
            //0x0001 | 0x0002 | 0x0040);
            taskbarMonitorControl.Show();

            UpdatePosition(tb);

            // Only mark the control as attached after the parent and initial
            // placement succeeded. If anything above throws, the next timer
            // tick disposes this partial control and creates a fresh one.
            taskbarMonitorControl.Name = expectedName;

            return true;
        }

        public bool ApplyOptions(Options options)
        {
            foreach (var taskbar in this.TaskbarList.ToList())
            {
                var control = taskbar.TaskbarMonitorControl;
                if (control == null || control.IsDisposed)
                    continue;

                try
                {
                    control.ApplyOptions(options);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Unable to apply options to a taskbar control: " + ex);
                }

                // The control width changes with HistorySize/DPI, so make sure
                // every taskbar is repositioned as well.
                UpdatePosition(taskbar, true);
            }
            return true;
        }
         
        private void HookEvents()
        {
            if (g_hook.Count > 0)
                return;
            
            Debug.WriteLine("HookEvents");
            Dictionary<AccessibleEvents, BLL.Win32Api.WinEventProc> events = InitializeWinEventToHandlerMap();

            //Hook window close event - close our HoverContorl on Target window close.
            BLL.Win32Api.WinEventProc eventHandler = new BLL.Win32Api.WinEventProc(events[AccessibleEvents.LocationChange].Invoke);

            GCHandle gch = GCHandle.Alloc(eventHandler);
            gchs.Add(gch);

            g_hook.Add(BLL.Win32Api.SetWinEventHook(AccessibleEvents.LocationChange,
       AccessibleEvents.LocationChange, IntPtr.Zero, eventHandler
       , 0, 0, BLL.Win32Api.SetWinEventHookParameter.WINEVENT_OUTOFCONTEXT));

            //Hook window close event - close our HoverContorl on Target window close.
            eventHandler = new BLL.Win32Api.WinEventProc(events[AccessibleEvents.Destroy].Invoke);

            gch = GCHandle.Alloc(eventHandler);
            gchs.Add(gch);

            g_hook.Add(BLL.Win32Api.SetWinEventHook(AccessibleEvents.Destroy,
                AccessibleEvents.Destroy, IntPtr.Zero, eventHandler
                , 0, 0, BLL.Win32Api.SetWinEventHookParameter.WINEVENT_OUTOFCONTEXT));
        }

        private void UnhookEvents()
        {
            Debug.WriteLine("UnhookEvents");

            foreach (var gh in g_hook)
            {
                try
                {
                    if (gh != IntPtr.Zero)
                        BLL.Win32Api.UnhookWinEvent(gh);
                }
                catch { }
            }
            g_hook.Clear();

            foreach (var gch in gchs)
            {
                try
                {
                    if (gch.IsAllocated)
                        gch.Free();
                }
                catch { }
            }
            gchs.Clear();
        }
         

        public void RemoveControls()
        {
            Debug.WriteLine("RemoveControls");
            UnhookEvents();

            foreach (var taskbar in TaskbarList.ToList())
            {
                DisposeTaskbarControl(taskbar);
                try
                {
                    BLL.WindowList.InvalidateRect(taskbar.TargetWnd, IntPtr.Zero, true);
                }
                catch { }
            }

            TaskbarList.Clear();
            MainTaskbar = null;
        }

        private void DisposeTaskbarControl(Taskbar taskbar)
        {
            if (taskbar == null)
                return;

            var control = taskbar.TaskbarMonitorControl;
            if (control == null)
                return;

            Action dispose = delegate
            {
                try { control.Hide(); } catch { }
                try { control.Dispose(); } catch { }
            };

            try
            {
                if (control.InvokeRequired)
                    control.Invoke(dispose);
                else
                    dispose();
            }
            catch
            {
                // The control can already be gone during an Explorer restart.
                try { control.Dispose(); } catch { }
            }
        }

        private void RemoveControl(Taskbar taskbar)
        {
            Debug.WriteLine("RemoveControls");
            if (taskbar == null)
                return;

            DisposeTaskbarControl(taskbar);

            try
            {
                BLL.WindowList.InvalidateRect(taskbar.TargetWnd, IntPtr.Zero, true);
            }
            catch { }

            TaskbarList.Remove(taskbar);
            if (taskbar == MainTaskbar)
                MainTaskbar = null;
        }
         
        private Dictionary<AccessibleEvents, BLL.Win32Api.WinEventProc> InitializeWinEventToHandlerMap()
        {
            Dictionary<AccessibleEvents, BLL.Win32Api.WinEventProc> dictionary = new Dictionary<AccessibleEvents, BLL.Win32Api.WinEventProc>
            {
                { AccessibleEvents.LocationChange, new BLL.Win32Api.WinEventProc(this.LocationChangedCallback) },
                { AccessibleEvents.Destroy, new BLL.Win32Api.WinEventProc(this.DestroyCallback) }
            };
            return dictionary;
        }

        private void DestroyCallback(IntPtr winEventHookHandle, AccessibleEvents accEvent, IntPtr windowHandle, int objectId, int childId, uint eventThreadId, uint eventTimeInMilliseconds)
        {            
            if (accEvent == AccessibleEvents.Destroy)
            {
                
                var taskbar = TaskbarList.Where(x => x.TargetWnd.ToInt32() == windowHandle.ToInt32()).SingleOrDefault();
                if(taskbar != null)
                {
                    bool wasMainTaskbar = taskbar.IsMainTaskbar;
                    RemoveControl(taskbar);

                    // Do not block the UI thread waiting for a new taskbar.
                    // If it is not ready yet the 4 second timer retries.
                    if (wasMainTaskbar)
                    {
                        AddControlsToTaskbars();
                    }
                }
            } 
        }
     
        private void LocationChangedCallback(IntPtr winEventHookHandle, AccessibleEvents accEvent, IntPtr windowHandle, int objectId, int childId, uint eventThreadId, uint eventTimeInMilliseconds)
        {
            if (accEvent == AccessibleEvents.LocationChange)
            {
                var taskbar = TaskbarList.Where(x => x.TrayWnd.ToInt32() == windowHandle.ToInt32()).SingleOrDefault();
                if (taskbar != null)
                {
                    UpdatePosition(taskbar);
                }
            }
        }
         
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            try
            {
                timer.Stop();
                timer.Tick -= Timer_Tick;
                timer.Dispose();
            }
            catch { }

            if (this.Monitor != null)
                this.Monitor.OnOptionsUpdated -= Monitor_OnOptionsUpdated;

            // Called from the UI thread at shutdown (Program's finally block),
            // so the controls are disposed directly instead of using Invoke.
            RemoveControls();

            lock (instanceLock)
            {
                if (_instance == this)
                    _instance = null;
            }
        }

        private static TaskbarManager _instance = null;
        private static readonly object instanceLock = new object();

        public static TaskbarManager GetInstance()
        {
            lock (instanceLock)
            {
                if (_instance == null)
                    _instance = new TaskbarManager();
                return _instance;
            }
        }
    }
}
