using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// Native deaths — an access violation in interop, a stack overflow, a
    /// FailFast, heap corruption — end a .NET process before any managed
    /// handler runs, and a dump written from inside a crashing process is
    /// not one to trust. So the OS writes it: Windows Error Reporting's
    /// LocalDumps, registered per executable under the user's own hive (no
    /// elevation), and the dumps it leaves are picked up at the next start.
    /// From each one this reads what an issue needs — the exception code,
    /// the faulting address, the module it fell in and that module's debug
    /// id — and nothing else; the dump itself is not uploaded.
    /// </summary>
    internal static class WerDumps
    {
        private const string LocalDumpsKey = @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\";
        private const int MaxDumpsKept = 5;

        /// <summary>Points WER at `dumpDirectory` for this exe. False where
        /// the registry is out of reach (not Windows, an AppContainer, a
        /// locked-down host), and the session-file detection stands alone.</summary>
        internal static bool Register(string dumpDirectory)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

            try
            {
                var exe = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "");

                if (string.IsNullOrEmpty(exe)) return false;

                Directory.CreateDirectory(dumpDirectory);

                return Registry.Write(LocalDumpsKey + exe, dumpDirectory, MaxDumpsKept);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Every dump written since `sinceMs`, oldest first, read and
        /// then deleted. A dump of a managed exception (the CLR's own code)
        /// is skipped when the run already wrote that crash itself.</summary>
        internal static List<Dictionary<string, object>> Take(string dumpDirectory, long sinceMs, bool managedCrashWritten)
        {
            var found = new List<Dictionary<string, object>>();

            if (!Directory.Exists(dumpDirectory)) return found;

            var files = Directory.GetFiles(dumpDirectory, "*.dmp");
            Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));

            foreach (var file in files)
            {
                var writtenMs = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();

                if (writtenMs < sinceMs)
                {
                    // Older than the run being settled: not its death; gone
                    // so the folder cannot fill.
                    DiskQueue.TryDelete(file);
                    continue;
                }

                Dictionary<string, object> read = null;

                try
                {
                    read = Minidump.Read(file);
                }
                catch (Exception)
                {
                    // A truncated or foreign dump says nothing.
                }

                DiskQueue.TryDelete(file);

                if (read == null) continue;

                if (managedCrashWritten && (string) read["type"] == "CLR_EXCEPTION") continue;

                read["writtenAt"] = writtenMs;
                found.Add(read);
            }

            return found;
        }

        /// <summary>advapi32 by hand: Microsoft.Win32.Registry is not in
        /// netstandard2.0, and a package for two values is not worth it.</summary>
        private static class Registry
        {
            private static readonly IntPtr HkeyCurrentUser = new IntPtr(unchecked((int) 0x80000001));
            private const int KeyWrite = 0x20006;
            private const int RegExpandSz = 2;
            private const int RegDword = 4;

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern int RegCreateKeyExW(IntPtr root, string subKey, int reserved, string className,
                int options, int desired, IntPtr securityAttributes, out IntPtr result, out int disposition);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern int RegSetValueExW(IntPtr key, string valueName, int reserved, int type,
                byte[] data, int size);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern int RegCloseKey(IntPtr key);

            internal static bool Write(string subKey, string dumpFolder, int dumpCount)
            {
                if (RegCreateKeyExW(HkeyCurrentUser, subKey, 0, null, 0, KeyWrite, IntPtr.Zero, out var key, out _) != 0)
                {
                    return false;
                }

                try
                {
                    var folder = Encoding.Unicode.GetBytes(dumpFolder + "\0");
                    var ok = RegSetValueExW(key, "DumpFolder", 0, RegExpandSz, folder, folder.Length) == 0;
                    // 1: a minidump — enough for the exception record and the
                    // module list, small enough to leave on a user's disk.
                    ok &= RegSetValueExW(key, "DumpType", 0, RegDword, BitConverter.GetBytes(1), 4) == 0;
                    ok &= RegSetValueExW(key, "DumpCount", 0, RegDword, BitConverter.GetBytes(dumpCount), 4) == 0;

                    return ok;
                }
                finally
                {
                    RegCloseKey(key);
                }
            }
        }

        /// <summary>The parts of a minidump an issue is made of. The format is
        /// the public MINIDUMP_* layout; only the exception and module streams
        /// are read.</summary>
        internal static class Minidump
        {
            private const uint Signature = 0x504D444D; // "MDMP"
            private const uint ModuleListStream = 4;
            private const uint ExceptionStream = 6;

            internal static Dictionary<string, object> Read(string path)
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream))
                {
                    return Read(reader);
                }
            }

            internal static Dictionary<string, object> Read(BinaryReader reader)
            {
                var stream = reader.BaseStream;

                if (stream.Length < 32 || reader.ReadUInt32() != Signature) return null;

                stream.Position = 8;
                var streamCount = reader.ReadUInt32();
                var directoryRva = reader.ReadUInt32();

                uint exceptionRva = 0, exceptionSize = 0, modulesRva = 0;

                for (var i = 0; i < streamCount && i < 64; i++)
                {
                    stream.Position = directoryRva + i * 12;
                    var type = reader.ReadUInt32();
                    var size = reader.ReadUInt32();
                    var rva = reader.ReadUInt32();

                    if (type == ExceptionStream)
                    {
                        exceptionRva = rva;
                        exceptionSize = size;
                    }
                    else if (type == ModuleListStream)
                    {
                        modulesRva = rva;
                    }
                }

                if (exceptionRva == 0 || exceptionSize < 32) return null;

                // MINIDUMP_EXCEPTION_STREAM: ThreadId, alignment, then the record.
                stream.Position = exceptionRva;
                var threadId = reader.ReadUInt32();
                stream.Position += 4;
                var code = reader.ReadUInt32();
                stream.Position += 4 + 8; // ExceptionFlags, ExceptionRecord
                var address = reader.ReadUInt64();
                var parameterCount = reader.ReadUInt32();
                stream.Position += 4;
                var parameters = new ulong[15];

                for (var i = 0; i < 15; i++) parameters[i] = reader.ReadUInt64();

                var modules = modulesRva != 0 ? Modules(reader, modulesRva) : new List<Module>();
                Module hit = null;

                foreach (var module in modules)
                {
                    if (address >= module.Base && address < module.Base + module.Size)
                    {
                        hit = module;
                        break;
                    }
                }

                var name = CodeName(code);
                var message = Describe(code, address, parameterCount, parameters, hit);
                var frames = new List<object>();

                if (hit != null)
                {
                    var frame = new Dictionary<string, object>
                    {
                        ["module"] = Path.GetFileName(hit.Name),
                        ["function"] = "0x" + (address - hit.Base).ToString("x", CultureInfo.InvariantCulture),
                        ["instructionAddr"] = "0x" + address.ToString("x", CultureInfo.InvariantCulture),
                        ["relativeAddr"] = "0x" + (address - hit.Base).ToString("x", CultureInfo.InvariantCulture),
                        ["image"] = hit.Name,
                    };

                    if (hit.DebugId != null) frame["buildId"] = hit.DebugId;

                    frames.Add(frame);
                }

                return new Dictionary<string, object>
                {
                    ["type"] = name,
                    ["message"] = message,
                    ["frames"] = frames,
                    ["threadId"] = threadId,
                    ["code"] = "0x" + code.ToString("x8", CultureInfo.InvariantCulture),
                    ["address"] = "0x" + address.ToString("x", CultureInfo.InvariantCulture),
                    ["moduleCount"] = modules.Count,
                };
            }

            private sealed class Module
            {
                internal ulong Base;
                internal ulong Size;
                internal string Name;
                internal string DebugId;
            }

            private static List<Module> Modules(BinaryReader reader, uint rva)
            {
                var stream = reader.BaseStream;
                stream.Position = rva;
                var count = reader.ReadUInt32();
                var modules = new List<Module>();

                for (var i = 0; i < count && i < 4096; i++)
                {
                    // MINIDUMP_MODULE is 108 bytes: base, size, checksum,
                    // timestamp, name rva, VS_FIXEDFILEINFO (52), CvRecord,
                    // MiscRecord, two reserved.
                    stream.Position = rva + 4 + i * 108;
                    var module = new Module
                    {
                        Base = reader.ReadUInt64(),
                        Size = reader.ReadUInt32(),
                    };
                    stream.Position += 8; // CheckSum, TimeDateStamp
                    var nameRva = reader.ReadUInt32();
                    stream.Position += 52;
                    var cvSize = reader.ReadUInt32();
                    var cvRva = reader.ReadUInt32();

                    module.Name = ReadString(reader, nameRva);

                    if (cvRva != 0 && cvSize >= 24)
                    {
                        stream.Position = cvRva;

                        if (reader.ReadUInt32() == 0x53445352) // "RSDS"
                        {
                            var guid = new Guid(reader.ReadBytes(16));
                            var age = reader.ReadUInt32();
                            module.DebugId = DebugId.Format(guid, age);
                        }
                    }

                    modules.Add(module);
                }

                return modules;
            }

            private static string ReadString(BinaryReader reader, uint rva)
            {
                if (rva == 0) return "";

                reader.BaseStream.Position = rva;
                var length = reader.ReadUInt32();

                if (length > 4096) return "";

                return Encoding.Unicode.GetString(reader.ReadBytes((int) length));
            }

            internal static string CodeName(uint code)
            {
                switch (code)
                {
                    case 0xC0000005: return "EXCEPTION_ACCESS_VIOLATION";
                    case 0xC00000FD: return "EXCEPTION_STACK_OVERFLOW";
                    case 0xC0000094: return "EXCEPTION_INT_DIVIDE_BY_ZERO";
                    case 0xC000001D: return "EXCEPTION_ILLEGAL_INSTRUCTION";
                    case 0xC0000409: return "STATUS_STACK_BUFFER_OVERRUN";
                    case 0xC0000374: return "STATUS_HEAP_CORRUPTION";
                    case 0xC0000008: return "STATUS_INVALID_HANDLE";
                    case 0xC000008C: return "EXCEPTION_ARRAY_BOUNDS_EXCEEDED";
                    case 0xC0000096: return "EXCEPTION_PRIV_INSTRUCTION";
                    case 0xC000000D: return "STATUS_INVALID_PARAMETER";
                    case 0x80000003: return "EXCEPTION_BREAKPOINT";
                    case 0xE0434352: return "CLR_EXCEPTION";
                    case 0xE06D7363: return "CPP_EXCEPTION";
                    default: return "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
                }
            }

            private static string Describe(uint code, ulong address, uint parameterCount, ulong[] parameters, Module hit)
            {
                var where = hit == null
                    ? "0x" + address.ToString("x", CultureInfo.InvariantCulture)
                    : Path.GetFileName(hit.Name) + "+0x" + (address - hit.Base).ToString("x", CultureInfo.InvariantCulture);

                if (code == 0xC0000005 && parameterCount >= 2)
                {
                    var kind = parameters[0] == 0 ? "reading" : parameters[0] == 1 ? "writing" : "executing";

                    return CodeName(code) + " " + kind + " 0x" + parameters[1].ToString("x", CultureInfo.InvariantCulture)
                           + " at " + where;
                }

                return CodeName(code) + " at " + where;
            }
        }
    }
}
