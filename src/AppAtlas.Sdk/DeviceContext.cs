using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AppAtlas.Sdk
{
    /// <summary>
    /// The device and app facts every module shares, snapshotted once at
    /// start and carried in each envelope's header as `device` and `app`
    /// blocks. The common keys are the ones every SDK writes, in the same
    /// order; `runtime` is this platform's own extra. All non-identifying.
    /// Every probe is guarded: each of these APIs throws somewhere (Mono,
    /// IL2CPP, UWP — the server repo's docs/sdk-cautions.md §3).
    /// </summary>
    internal static class DeviceContext
    {
        internal static Dictionary<string, object> Snapshot()
        {
            var device = new Dictionary<string, object>
            {
                ["os"] = "windows",
                // ntdll's own word where there is one: Environment.OSVersion
                // answers 6.2 on .NET Framework without a manifest.
                ["osVersion"] = Probe(() => Crash.DeviceFacts.WindowsVersion.Build())
                                ?? Probe(() => Environment.OSVersion.Version.ToString()) ?? "unknown",
                // No `model`: reading it needs WMI, which netstandard2.0 does
                // not carry and UWP would refuse. Absent beats invented.
                ["arch"] = Probe(() => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()) ?? "unknown",
                ["locale"] = Probe(() => CultureInfo.CurrentCulture.Name) ?? "unknown",
                ["timezone"] = Probe(() => TimeZoneInfo.Local.Id) ?? "unknown",
                ["runtime"] = Probe(() => RuntimeInformation.FrameworkDescription) ?? "unknown",
            };

            var app = new Dictionary<string, object>();
            var entry = Probe(() => Assembly.GetEntryAssembly());
            var version = Probe(() => entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
                          ?? Probe(() => entry?.GetName().Version?.ToString());
            var build = Probe(() => entry?.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version)
                        ?? Probe(() => entry?.GetName().Version?.ToString());

            if (version != null) app["version"] = version;
            if (build != null) app["build"] = build;

            return new Dictionary<string, object> {["device"] = device, ["app"] = app};
        }

        private static string Probe(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Assembly Probe(Func<Assembly> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
