using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using TaskbarMonitor.Counters;

namespace TaskbarMonitor.Usage
{
    /// <summary>
    /// Collects the top resource users while the user hovers a taskbar
    /// counter. Sampling only runs while a popup is visible, so the normal
    /// taskbar-monitor polling path is left untouched.
    /// </summary>
    public sealed class ProcessUsageService : IDisposable
    {
        private const string ProcessV2ReadPath = @"\Process V2(*)\IO Read Bytes/sec";
        private const string ProcessV2WritePath = @"\Process V2(*)\IO Write Bytes/sec";
        private const string ProcessReadPath = @"\Process(*)\IO Read Bytes/sec";
        private const string ProcessWritePath = @"\Process(*)\IO Write Bytes/sec";
        private const string GpuEnginePath = @"\GPU Engine(*)\Utilization Percentage";
        private const string GpuMemoryPath = @"\GPU Process Memory(*)\Dedicated Usage";
        private const string NetworkSentPath = @"\Network Interface(*)\Bytes Sent/sec";
        private const string NetworkReceivedPath = @"\Network Interface(*)\Bytes Received/sec";

        private const int SampleIntervalMilliseconds = 600;
        private const int FirstSampleDelayMilliseconds = 60;

        private readonly object stateLock = new object();
        private readonly object sampleLock = new object();
        private readonly object snapshotLock = new object();

        private readonly Dictionary<string, ProcessUsageSnapshot> snapshots =
            new Dictionary<string, ProcessUsageSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<int, CpuSample> lastCpu =
            new Dictionary<int, CpuSample>();

        private PerformanceCounterReader reader;
        private System.Threading.Timer timer;
        private volatile bool active;
        private bool haveCpuBaseline;
        private bool diskFallbackAdded;
        private DateTime lastSampleUtc;

        private sealed class CpuSample
        {
            public double CpuMilliseconds;
        }

        public ProcessUsageService()
        {
        }

        public static string GetTitle(string counterName)
        {
            switch (counterName)
            {
                case "CPU":
                    return "CPU - Top 5 processes";
                case "MEM":
                    return "MEM - Top 5 processes";
                case "DISK":
                    return "DISK - Top 5 process I/O (approx.)";
                case "NET":
                    return "NET - Top 5 adapters";
                case "GPU 3D":
                    return "GPU 3D - Top 5 processes";
                case "GPU MEM":
                    return "GPU MEM - Top 5 processes";
                default:
                    return (counterName ?? "Usage") + " - Top 5";
            }
        }

        public void Start()
        {
            lock (stateLock)
            {
                if (active)
                    return;

                active = true;
                haveCpuBaseline = false;
                diskFallbackAdded = false;
                lastSampleUtc = DateTime.MinValue;

                lock (snapshotLock)
                {
                    snapshots.Clear();
                }

                lastCpu.Clear();
                timer = new System.Threading.Timer(Sample, null,
                    FirstSampleDelayMilliseconds, SampleIntervalMilliseconds);
            }
        }

        public void Stop()
        {
            lock (stateLock)
            {
                active = false;
                if (timer != null)
                {
                    try { timer.Dispose(); } catch { }
                    timer = null;
                }
            }

            // Wait for an in-flight sample before closing the PDH query.
            lock (sampleLock)
            {
                if (reader != null)
                {
                    try { reader.Dispose(); } catch { }
                    reader = null;
                }
            }

            lastCpu.Clear();
            haveCpuBaseline = false;

            lock (snapshotLock)
            {
                snapshots.Clear();
            }
        }

        public ProcessUsageSnapshot GetSnapshot(string counterName)
        {
            if (string.IsNullOrEmpty(counterName))
                return null;

            lock (snapshotLock)
            {
                ProcessUsageSnapshot result;
                return snapshots.TryGetValue(counterName, out result) ? result : null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void Sample(object state)
        {
            lock (sampleLock)
            {
                if (!active)
                    return;

                Dictionary<string, float> values = null;

                try
                {
                    if (reader == null)
                        reader = CreateReader();

                    values = reader.ReadCounters();

                    if (!diskFallbackAdded && !HasAnyValue(values, ProcessV2ReadPath)
                        && !HasAnyValue(values, ProcessV2WritePath))
                    {
                        // Process V2 is available on current Windows versions,
                        // but keep the old Process counter as a fallback for
                        // older systems.
                        diskFallbackAdded = true;
                        reader.AddPath(ProcessReadPath);
                        reader.AddPath(ProcessWritePath);
                        values = reader.ReadCounters();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Unable to read process usage performance counters: " + ex);
                }

                try
                {
                    SampleCpuAndMemory(values);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Unable to read process usage: " + ex);
                }
            }
        }

        private PerformanceCounterReader CreateReader()
        {
            var result = new PerformanceCounterReader(TimeSpan.FromSeconds(30));
            result.AddPath(ProcessV2ReadPath);
            result.AddPath(ProcessV2WritePath);
            result.AddPath(GpuEnginePath);
            result.AddPath(GpuMemoryPath);
            result.AddPath(NetworkSentPath);
            result.AddPath(NetworkReceivedPath);
            return result;
        }

        private void SampleCpuAndMemory(Dictionary<string, float> values)
        {
            var cpuValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var memoryValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var nextCpu = new Dictionary<int, CpuSample>();

            var now = DateTime.UtcNow;
            double elapsedMilliseconds = lastSampleUtc == DateTime.MinValue
                ? 0
                : (now - lastSampleUtc).TotalMilliseconds;
            bool cpuReady = haveCpuBaseline && elapsedMilliseconds >= 100;

            Process[] processes = null;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Unable to enumerate processes: " + ex);
            }

            if (processes != null)
            {
                foreach (var process in processes)
                {
                    try
                    {
                        string processName = null;
                        try { processName = process.ProcessName; } catch { }
                        if (string.IsNullOrEmpty(processName))
                            continue;

                        long memory = 0;
                        try { memory = process.PrivateMemorySize64; } catch { }
                        if (memory > 0)
                            AddValue(memoryValues, processName, memory);

                        double cpuMilliseconds = 0;
                        try { cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds; } catch { }

                        int processId = process.Id;
                        CpuSample previous;
                        if (cpuReady && lastCpu.TryGetValue(processId, out previous)
                            && cpuMilliseconds >= previous.CpuMilliseconds)
                        {
                            double delta = cpuMilliseconds - previous.CpuMilliseconds;
                            if (delta > 0 && !IsIdleProcess(processName))
                            {
                                double percent = delta / elapsedMilliseconds * 100.0
                                    / Math.Max(1, Environment.ProcessorCount);
                                if (percent > 100.0)
                                    percent = 100.0;
                                AddValue(cpuValues, processName, percent);
                            }
                        }

                        if (cpuMilliseconds > 0)
                        {
                            nextCpu[processId] = new CpuSample
                            {
                                CpuMilliseconds = cpuMilliseconds
                            };
                        }
                    }
                    catch
                    {
                        // A process can exit between enumeration and reading.
                    }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }

            lastCpu.Clear();
            foreach (var item in nextCpu)
                lastCpu[item.Key] = item.Value;

            lastSampleUtc = now;
            haveCpuBaseline = true;

            var processNameCache = new Dictionary<int, string>();

            List<ProcessUsageRow> cpuRows = cpuReady
                ? BuildTopRows(cpuValues, FormatPercent, 0.05)
                : new List<ProcessUsageRow>();
            List<ProcessUsageRow> memoryRows = BuildTopRows(memoryValues, FormatBytes, 1);
            List<ProcessUsageRow> diskRows = BuildTopRows(BuildDiskValues(values), FormatRate, 1);
            List<ProcessUsageRow> networkRows = BuildTopRows(BuildNetworkValues(values), FormatRate, 1);
            List<ProcessUsageRow> gpuRows = BuildTopRows(
                BuildGpuValues(values, processNameCache), FormatPercent, 0.05);
            List<ProcessUsageRow> gpuMemoryRows = BuildTopRows(
                BuildGpuMemoryValues(values, processNameCache), FormatBytes, 1);

            var nextSnapshots = new Dictionary<string, ProcessUsageSnapshot>(StringComparer.Ordinal);
            nextSnapshots["CPU"] = new ProcessUsageSnapshot(
                "CPU", GetTitle("CPU"), cpuRows, cpuReady);
            nextSnapshots["MEM"] = new ProcessUsageSnapshot(
                "MEM", GetTitle("MEM"), memoryRows, true);
            nextSnapshots["DISK"] = new ProcessUsageSnapshot(
                "DISK", GetTitle("DISK"), diskRows, true);
            nextSnapshots["NET"] = new ProcessUsageSnapshot(
                "NET", GetTitle("NET"), networkRows, true);
            nextSnapshots["GPU 3D"] = new ProcessUsageSnapshot(
                "GPU 3D", GetTitle("GPU 3D"), gpuRows, true);
            nextSnapshots["GPU MEM"] = new ProcessUsageSnapshot(
                "GPU MEM", GetTitle("GPU MEM"), gpuMemoryRows, true);

            lock (snapshotLock)
            {
                snapshots.Clear();
                foreach (var item in nextSnapshots)
                    snapshots[item.Key] = item.Value;
            }
        }

        private static Dictionary<string, double> BuildDiskValues(Dictionary<string, float> values)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
                return result;

            foreach (var item in values)
            {
                string instance;
                if (!TryGetInstance(item.Key, ProcessV2ReadPath, out instance)
                    && !TryGetInstance(item.Key, ProcessV2WritePath, out instance)
                    && !TryGetInstance(item.Key, ProcessReadPath, out instance)
                    && !TryGetInstance(item.Key, ProcessWritePath, out instance))
                    continue;

                string processName = NormalizeProcessInstance(instance);
                if (IsIdleProcess(processName) || IsTotalInstance(processName))
                    continue;

                AddValue(result, processName, item.Value);
            }

            return result;
        }

        private static Dictionary<string, double> BuildNetworkValues(Dictionary<string, float> values)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
                return result;

            foreach (var item in values)
            {
                string instance;
                if (!TryGetInstance(item.Key, NetworkSentPath, out instance)
                    && !TryGetInstance(item.Key, NetworkReceivedPath, out instance))
                    continue;

                if (IsIgnoredNetworkAdapter(instance))
                    continue;

                AddValue(result, instance, item.Value);
            }

            return result;
        }

        private static Dictionary<string, double> BuildGpuValues(
            Dictionary<string, float> values, Dictionary<int, string> processNameCache)
        {
            var perProcess = new Dictionary<int, double>();
            if (values != null)
            {
                foreach (var item in values)
                {
                    string instance;
                    if (!TryGetInstance(item.Key, GpuEnginePath, out instance))
                        continue;

                    if (instance.IndexOf("engtype_3d", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    int processId = ParseProcessId(instance);
                    if (processId <= 0)
                        continue;

                    double current;
                    if (!perProcess.TryGetValue(processId, out current) || item.Value > current)
                        perProcess[processId] = item.Value;
                }
            }

            var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in perProcess)
            {
                string processName = GetProcessName(item.Key, processNameCache);
                if (IsIdleProcess(processName))
                    continue;
                AddValue(byName, processName, item.Value);
            }

            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in byName)
                result[item.Key] = Math.Min(100.0, item.Value);

            return result;
        }

        private static Dictionary<string, double> BuildGpuMemoryValues(
            Dictionary<string, float> values, Dictionary<int, string> processNameCache)
        {
            var perProcess = new Dictionary<int, double>();
            if (values != null)
            {
                foreach (var item in values)
                {
                    string instance;
                    if (!TryGetInstance(item.Key, GpuMemoryPath, out instance))
                        continue;

                    int processId = ParseProcessId(instance);
                    if (processId <= 0)
                        continue;

                    AddValue(perProcess, processId, item.Value);
                }
            }

            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in perProcess)
            {
                string processName = GetProcessName(item.Key, processNameCache);
                if (IsIdleProcess(processName))
                    continue;
                AddValue(result, processName, item.Value);
            }

            return result;
        }

        private static List<ProcessUsageRow> BuildTopRows(
            Dictionary<string, double> values, Func<double, string> formatter, double minimum)
        {
            var result = new List<ProcessUsageRow>();
            if (values == null)
                return result;

            foreach (var item in values
                .Where(x => x.Value > minimum)
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5))
            {
                result.Add(new ProcessUsageRow(item.Key, formatter(item.Value)));
            }

            return result;
        }

        private static string GetProcessName(int processId, Dictionary<int, string> cache)
        {
            string name;
            if (cache.TryGetValue(processId, out name))
                return name;

            try
            {
                using (var process = Process.GetProcessById(processId))
                    name = process.ProcessName;
            }
            catch
            {
                name = "PID " + processId.ToString(CultureInfo.InvariantCulture);
            }

            cache[processId] = name;
            return name;
        }

        private static string NormalizeProcessInstance(string instance)
        {
            if (string.IsNullOrEmpty(instance))
                return null;

            // Process V2 uses "name:pid". The older Process counter uses
            // "name#1" for duplicate instances.
            int colon = instance.LastIndexOf(':');
            if (colon > 0 && IsDigits(instance.Substring(colon + 1)))
                return instance.Substring(0, colon);

            int hash = instance.LastIndexOf('#');
            if (hash > 0 && IsDigits(instance.Substring(hash + 1)))
                return instance.Substring(0, hash);

            return instance;
        }

        private static int ParseProcessId(string instance)
        {
            if (string.IsNullOrEmpty(instance)
                || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
                return -1;

            int start = 4;
            int end = instance.IndexOf('_', start);
            if (end <= start)
                return -1;

            int processId;
            return int.TryParse(instance.Substring(start, end - start),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out processId)
                ? processId
                : -1;
        }

        private static bool TryGetInstance(string key, string path, out string instance)
        {
            instance = null;
            if (string.IsNullOrEmpty(key))
                return false;

            string prefix = path + ":";
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            instance = key.Substring(prefix.Length);
            return true;
        }

        private static bool HasAnyValue(Dictionary<string, float> values, string path)
        {
            if (values == null)
                return false;

            string prefix = path + ":";
            foreach (var key in values.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool IsDigits(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                    return false;
            }

            return true;
        }

        private static bool IsIdleProcess(string processName)
        {
            return string.Equals(processName, "Idle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(processName, "System Idle Process", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTotalInstance(string processName)
        {
            return string.Equals(processName, "_Total", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIgnoredNetworkAdapter(string instance)
        {
            if (string.IsNullOrEmpty(instance))
                return true;

            return instance.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0
                || instance.IndexOf("isatap", StringComparison.OrdinalIgnoreCase) >= 0
                || instance.IndexOf("Pseudo-Interface", StringComparison.OrdinalIgnoreCase) >= 0
                || instance.IndexOf("Teredo", StringComparison.OrdinalIgnoreCase) >= 0
                || instance.StartsWith("_Total", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddValue(Dictionary<string, double> values, string key, double value)
        {
            double current;
            values.TryGetValue(key, out current);
            values[key] = current + value;
        }

        private static void AddValue(Dictionary<int, double> values, int key, double value)
        {
            double current;
            values.TryGetValue(key, out current);
            values[key] = current + value;
        }

        private static string FormatPercent(double value)
        {
            return value.ToString("0.0", CultureInfo.CurrentCulture) + "%";
        }

        private static string FormatBytes(double value)
        {
            const double kilobyte = 1024.0;
            const double megabyte = kilobyte * 1024.0;
            const double gigabyte = megabyte * 1024.0;

            if (value >= gigabyte)
                return (value / gigabyte).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
            if (value >= megabyte)
                return (value / megabyte).ToString("0", CultureInfo.CurrentCulture) + " MB";
            return (value / kilobyte).ToString("0", CultureInfo.CurrentCulture) + " KB";
        }

        private static string FormatRate(double value)
        {
            const double kilobyte = 1024.0;
            const double megabyte = kilobyte * 1024.0;

            if (value >= megabyte)
                return (value / megabyte).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s";
            return (value / kilobyte).ToString("0.0", CultureInfo.CurrentCulture) + " KB/s";
        }
    }
}
