using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TaskbarMonitor.Counters;

namespace TaskbarMonitor
{
    public class Monitor: IDisposable
    {
        private readonly PerformanceCounterReader reader;

        public delegate void NotifyUpdate();
        public event NotifyUpdate OnMonitorUpdated;

        public delegate void OptionsUpdate();
        public event OptionsUpdate OnOptionsUpdated;

        public Options Options { get; private set; }

        // Kept public for compatibility with the UI. The list itself is only
        // mutated while countersLock is held; use GetCounter/HasCounters.
        public List<Counters.ICounter> Counters;

        private readonly object countersLock = new object();
        private readonly System.Timers.Timer pollingTimer;
        private volatile bool disposed;

        private static Monitor _instance = null;
        private static readonly object instanceLock = new object();
        public static Monitor GetInstance(Options opt)
        {
            lock (instanceLock)
            {
                if (_instance == null) _instance = new Monitor(opt);
            }
            return _instance;
        }

        private static readonly object updateLock = new object();
        private static bool updating = false;

        private Monitor(Options opt)
        {
            opt = opt ?? Options.DefaultOptions();
            reader = new PerformanceCounterReader(TimeSpan.FromSeconds(30));
            UpdateOptions(opt);

            pollingTimer = new System.Timers.Timer(Math.Max(1, opt.PollTime) * 1000);
            pollingTimer.Enabled = true;
            pollingTimer.Elapsed += PollingTimer_Elapsed;
            pollingTimer.Start();
        }

        public bool HasCounters
        {
            get
            {
                lock (countersLock)
                {
                    return Counters != null && Counters.Count > 0;
                }
            }
        }

        public ICounter GetCounter(string name)
        {
            if (name == null)
                return null;

            lock (countersLock)
            {
                return Counters == null ? null : Counters.FirstOrDefault(x => x.GetName() == name);
            }
        }

        public void UpdateOptions(Options opt)
        {
            if (opt == null)
                opt = Options.DefaultOptions();

            if (opt.CounterOptions == null)
                opt.CounterOptions = new Dictionary<string, CounterOptions>();

            lock (countersLock)
            {
                this.Options = opt;

                if (Counters == null)
                    Counters = new List<Counters.ICounter>();

                var counterNames = new List<string> { "CPU", "MEM", "DISK", "NET", "GPU 3D", "GPU MEM" };
                foreach (var counterName in counterNames)
                {
                    var q = Counters.FirstOrDefault(x => x.GetName() == counterName);
                    if (opt.CounterOptions.ContainsKey(counterName) && opt.CounterOptions[counterName] != null && q == null)
                    {
                        ICounter ct = null;
                        switch (counterName)
                        {
                            case "CPU":
                                ct = new Counters.CounterCPU(opt);
                                break;
                            case "GPU 3D":
                                ct = new Counters.CounterGPU(opt);
                                break;
                            case "GPU MEM":
                                ct = new Counters.CounterGPUMemory(opt);
                                break;
                            case "MEM":
                                ct = new Counters.CounterMemory(opt);
                                break;
                            case "DISK":
                                ct = new Counters.CounterDisk(opt);
                                break;
                            case "NET":
                                ct = new Counters.CounterNetwork(opt);
                                break;
                        }

                        if (ct == null)
                            continue;

                        try
                        {
                            ct.Initialize(reader);
                            Counters.Add(ct);
                        }
                        catch (Exception ex)
                        {
                            // A missing/broken performance counter must not take
                            // down the whole application.
                            Debug.WriteLine("Unable to initialize counter " + counterName + ": " + ex);
                            try { ct.Dispose(); } catch { }
                        }
                    }
                    else if (q != null && !opt.CounterOptions.ContainsKey(counterName))
                    {
                        try { q.Dispose(); } catch { }
                        Counters.Remove(q);
                    }
                }
            }

            // Fire outside the lock so subscribers can safely call back into
            // the Monitor if they need to.
            try
            {
                if (OnOptionsUpdated != null)
                    OnOptionsUpdated();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OnOptionsUpdated failed: " + ex);
            }
        }

        private void PollingTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (disposed)
                return;

            var currentOptions = Options;
            if (pollingTimer != null && currentOptions != null)
            {
                int interval = Math.Max(1, currentOptions.PollTime) * 1000;
                if (pollingTimer.Interval != interval)
                {
                    try { pollingTimer.Interval = interval; } catch { }
                }
            }

            lock (updateLock)
            {
                if (updating)
                    return;
                updating = true;
            }

            try
            {
                // Serialize counter reads with UpdateOptions: the settings UI
                // may add/remove counters on the UI thread while this runs.
                lock (countersLock)
                {
                    reader.ReadCounters();

                    if (Counters != null)
                    {
                        foreach (var ct in Counters.ToArray())
                        {
                            try
                            {
                                ct.Update();
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine("Counter update failed: " + ex);
                            }
                        }
                    }
                }

                try
                {
                    if (OnMonitorUpdated != null)
                        OnMonitorUpdated();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("OnMonitorUpdated failed: " + ex);
                }
            }
            catch (Exception ex)
            {
                // Exceptions on a System.Timers.Timer thread are fatal to the
                // process; keep the poll loop alive on transient failures.
                Debug.WriteLine("Polling failed: " + ex);
            }
            finally
            {
                lock (updateLock)
                {
                    updating = false;
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
                pollingTimer?.Stop();
                pollingTimer.Elapsed -= PollingTimer_Elapsed;
                pollingTimer?.Dispose();
            }
            catch { }

            lock (countersLock)
            {
                if (Counters != null)
                {
                    foreach (var ct in Counters)
                    {
                        try { ct.Dispose(); } catch { }
                    }
                    Counters.Clear();
                }

                try { reader.Dispose(); }
                catch (Exception ex) { Debug.WriteLine("Unable to dispose the counter reader: " + ex); }
            }

            lock (instanceLock)
            {
                if (_instance == this)
                    _instance = null;
            }
        }
    }
}
