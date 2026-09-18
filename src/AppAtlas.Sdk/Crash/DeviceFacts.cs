using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// The machine's state at the instant of a report, for its `context`.
    /// Every probe is guarded: each of these throws somewhere (Mono, IL2CPP,
    /// UWP — the server repo's docs/sdk-cautions.md §3), and a report goes
    /// without a fact sooner than not at all.
    /// </summary>
    internal static class DeviceFacts
    {
        internal static Dictionary<string, object> Now(string dataDir)
        {
            var facts = new Dictionary<string, object>();

            Put(facts, "memoryWorkingSetBytes", () => Process.GetCurrentProcess().WorkingSet64);
            Put(facts, "memoryPrivateBytes", () => Process.GetCurrentProcess().PrivateMemorySize64);
            Put(facts, "memoryManagedBytes", () => GC.GetTotalMemory(false));
            Put(facts, "memoryTotalBytes", () => TotalPhysicalMemory());
            Put(facts, "diskFreeBytes", () => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataDir))).AvailableFreeSpace);
            Put(facts, "processUptimeMs", () => (long) (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds);
            Put(facts, "bootTime", () => AtlasCore.Iso(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount));
            Put(facts, "is64BitProcess", () => Environment.Is64BitProcess);
            Put(facts, "processorCount", () => Environment.ProcessorCount);
            Put(facts, "runtime", () => RuntimeInformation.FrameworkDescription);
            Put(facts, "debugger", () => Debugger.IsAttached);
            Put(facts, "interactive", () => Environment.UserInteractive);
            Put(facts, "threadCount", () => Process.GetCurrentProcess().Threads.Count);
            Put(facts, "handleCount", () => Process.GetCurrentProcess().HandleCount);

            return facts;
        }

        /// <summary>The machine's memory, where the runtime tells: .NET Core
        /// 3+ through GC.GetGCMemoryInfo, by name so netstandard2.0 compiles.</summary>
        private static object TotalPhysicalMemory()
        {
            var info = typeof(GC).GetMethod("GetGCMemoryInfo", Type.EmptyTypes)?.Invoke(null, null);
            var total = info?.GetType().GetProperty("TotalAvailableMemoryBytes")?.GetValue(info);

            return total is long bytes && bytes > 0 ? (object) bytes : null;
        }

        private static void Put(Dictionary<string, object> facts, string name, Func<object> read)
        {
            try
            {
                var value = read();

                if (value != null) facts[name] = value;
            }
            catch (Exception)
            {
                // Absent beats invented.
            }
        }
    }
}
