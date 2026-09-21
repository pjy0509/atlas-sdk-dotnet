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
            Put(facts, "memoryTotalBytes", () => WindowsMemory.Total() ?? TotalPhysicalMemory());
            Put(facts, "memoryFreeBytes", () => WindowsMemory.Available());
            Put(facts, "osBuild", () => WindowsVersion.Build());
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

        /// <summary>kernel32's GlobalMemoryStatusEx: the machine's memory and
        /// what is free, with no runtime in between. Null off Windows.</summary>
        internal static class WindowsMemory
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct MemoryStatus
            {
                public uint Length;
                public uint MemoryLoad;
                public ulong TotalPhys;
                public ulong AvailPhys;
                public ulong TotalPageFile;
                public ulong AvailPageFile;
                public ulong TotalVirtual;
                public ulong AvailVirtual;
                public ulong AvailExtendedVirtual;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

            private static MemoryStatus? Read()
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

                var status = new MemoryStatus {Length = (uint) Marshal.SizeOf(typeof(MemoryStatus))};

                return GlobalMemoryStatusEx(ref status) ? status : (MemoryStatus?) null;
            }

            internal static object Total()
            {
                var status = Read();

                return status.HasValue ? (object) (long) status.Value.TotalPhys : null;
            }

            internal static object Available()
            {
                var status = Read();

                return status.HasValue ? (object) (long) status.Value.AvailPhys : null;
            }
        }

        /// <summary>The true Windows version, from ntdll's RtlGetVersion: what
        /// Environment.OSVersion lies about on .NET Framework without a
        /// manifest. "10.0.22631" shape. Null off Windows.</summary>
        internal static class WindowsVersion
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct VersionInfo
            {
                public uint Size;
                public uint Major;
                public uint Minor;
                public uint Build;
                public uint Platform;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                public string ServicePack;
            }

            [DllImport("ntdll.dll")]
            private static extern int RtlGetVersion(ref VersionInfo info);

            internal static string Build()
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

                var info = new VersionInfo {Size = (uint) Marshal.SizeOf(typeof(VersionInfo))};

                return RtlGetVersion(ref info) == 0 ? info.Major + "." + info.Minor + "." + info.Build : null;
            }
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
