using System;
using System.Collections.Generic;

namespace TaskbarMonitor.Usage
{
    /// <summary>
    /// One row in the process usage popup. The name is already grouped by
    /// executable name and the value is formatted for display.
    /// </summary>
    public sealed class ProcessUsageRow
    {
        public string Name { get; private set; }
        public string Value { get; private set; }

        public ProcessUsageRow(string name, string value)
        {
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
        }
    }

    /// <summary>
    /// Immutable snapshot of the top resource users for one taskbar counter.
    /// </summary>
    public sealed class ProcessUsageSnapshot
    {
        public string CounterName { get; private set; }
        public string Title { get; private set; }
        public IReadOnlyList<ProcessUsageRow> Rows { get; private set; }

        /// <summary>
        /// False while CPU usage is waiting for its first measurement interval.
        /// </summary>
        public bool IsReady { get; private set; }

        public ProcessUsageSnapshot(string counterName, string title, IEnumerable<ProcessUsageRow> rows, bool isReady)
        {
            CounterName = counterName ?? string.Empty;
            Title = title ?? string.Empty;
            Rows = rows == null
                ? (IReadOnlyList<ProcessUsageRow>)new List<ProcessUsageRow>()
                : (IReadOnlyList<ProcessUsageRow>)new List<ProcessUsageRow>(rows);
            IsReady = isReady;
        }

        public static ProcessUsageSnapshot Loading(string counterName, string title)
        {
            return new ProcessUsageSnapshot(counterName, title, null, false);
        }
    }
}
