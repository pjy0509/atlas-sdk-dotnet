using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// The process's top-level exception filter, taken from managed code:
    /// what Windows calls last, on the faulting thread, when nothing else
    /// handled an access violation in native code, a heap corruption, an
    /// illegal instruction. The runtime's own filter sat here first and is
    /// called after ours, so its Watson report and the WER dump still
    /// happen; ours writes the crash to disk in between, with the faulting
    /// thread's stack as the OS unwinder sees it (managed frames included,
    /// since the runtime registers its JIT code with the unwinder). A stack
    /// overflow and a FailFast bypass every filter and stay WER's to dump.
    /// </summary>
    internal static class NativeCrashFilter
    {
        internal const string Mechanism = "unhandledExceptionFilter";

        private const uint ClrException = 0xE0434352;
        private const int ContinueSearch = 0;
        private const uint FromAddress = 4;
        private const uint UnchangedRefcount = 2;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Filter(IntPtr pointers);

        [DllImport("kernel32.dll")]
        private static extern IntPtr SetUnhandledExceptionFilter(Filter filter);

        [DllImport("ntdll.dll")]
        private static extern ushort RtlCaptureStackBackTrace(uint skip, uint count, IntPtr[] frames, IntPtr hash);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder name, uint size);

        // The delegate must outlive the process: the OS holds only its thunk.
        private static Filter _keepAlive;
        private static IntPtr _previous;
        private static CrashReporter _reporter;
        private static readonly Dictionary<IntPtr, string> DebugIds = new Dictionary<IntPtr, string>();

        /// <summary>False off Windows, or where the filter cannot be set.</summary>
        internal static bool Install(CrashReporter reporter)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || _keepAlive != null) return false;

            try
            {
                _reporter = reporter;
                _keepAlive = OnException;
                _previous = SetUnhandledExceptionFilter(_keepAlive);

                return true;
            }
            catch (Exception)
            {
                _keepAlive = null;

                return false;
            }
        }

        private static int OnException(IntPtr pointers)
        {
            try
            {
                Report(pointers);
            }
            catch (Exception)
            {
                // A filter that fails must still let the process die its own death.
            }

            // Whoever was here first (the runtime) decides what happens next.
            if (_previous != IntPtr.Zero)
            {
                try
                {
                    return Marshal.GetDelegateForFunctionPointer<Filter>(_previous)(pointers);
                }
                catch (Exception)
                {
                }
            }

            return ContinueSearch;
        }

        private static void Report(IntPtr pointers)
        {
            if (pointers == IntPtr.Zero) return;

            // EXCEPTION_POINTERS: the record, then the context.
            var record = Marshal.ReadIntPtr(pointers);

            if (record == IntPtr.Zero) return;

            var code = (uint) Marshal.ReadInt32(record);

            // A managed exception took the managed path already.
            if (code == ClrException) return;

            var pointerSize = IntPtr.Size;
            // EXCEPTION_RECORD: code, flags, chained record, address, parameter count, parameters.
            var address = (ulong) Marshal.ReadIntPtr(record, 8 + pointerSize).ToInt64();
            var parameterCount = (uint) Marshal.ReadInt32(record, 8 + 2 * pointerSize);
            var parameters = new ulong[2];

            for (var i = 0; i < 2 && i < parameterCount; i++)
            {
                parameters[i] = (ulong) Marshal.ReadIntPtr(record, 8 + 2 * pointerSize + 4 + (pointerSize == 8 ? 4 : 0)
                                                                   + i * pointerSize).ToInt64();
            }

            var frames = Frames(address);
            var name = WerDumps.Minidump.CodeName(code);
            var top = frames.Count > 0 ? (Dictionary<string, object>) frames[0] : null;
            var where = top != null
                ? top["module"] + "+" + top["relativeAddr"]
                : "0x" + address.ToString("x", CultureInfo.InvariantCulture);
            var message = code == 0xC0000005 && parameterCount >= 2
                ? name + " " + (parameters[0] == 0 ? "reading" : parameters[0] == 1 ? "writing" : "executing")
                  + " 0x" + parameters[1].ToString("x", CultureInfo.InvariantCulture) + " at " + where
                : name + " at " + where;

            _reporter?.NativeCrash(name, message, frames, new Dictionary<string, object>
            {
                ["exceptionCode"] = "0x" + code.ToString("x8", CultureInfo.InvariantCulture),
                ["faultAddress"] = "0x" + address.ToString("x", CultureInfo.InvariantCulture),
                ["source"] = "SetUnhandledExceptionFilter",
            });
        }

        /// <summary>The faulting thread's stack, from where the fault was: the
        /// filter runs on that very thread, above the kernel's dispatcher,
        /// above the fault, so a walk from here reaches the frames that
        /// matter once the frames that do not are cut.</summary>
        private static List<object> Frames(ulong faultAddress)
        {
            var raw = new IntPtr[62];
            int count;

            try
            {
                count = RtlCaptureStackBackTrace(0, (uint) raw.Length, raw, IntPtr.Zero);
            }
            catch (Exception)
            {
                count = 0;
            }

            var frames = new List<object>();
            var start = -1;

            for (var i = 0; i < count && start < 0; i++)
            {
                if ((ulong) raw[i].ToInt64() == faultAddress) start = i;
            }

            if (start < 0)
            {
                // The faulting pc is not a return address on this stack (a
                // fault the dispatcher entered from): it leads, and the walk
                // follows from past the filter and the dispatcher.
                frames.Add(Frame(faultAddress));
                start = Math.Min(count, 4);
            }

            for (var i = start; i < count && frames.Count < CrashReport.MaxFrames; i++)
            {
                var address = (ulong) raw[i].ToInt64();

                // A return address is looked up one byte inside the call;
                // the faulting pc as it is.
                frames.Add(Frame(address == faultAddress ? address : address - 1));
            }

            return frames;
        }

        private static Dictionary<string, object> Frame(ulong address)
        {
            var frame = new Dictionary<string, object>
            {
                ["module"] = "?",
                ["function"] = "0x" + address.ToString("x", CultureInfo.InvariantCulture),
                ["instructionAddr"] = "0x" + address.ToString("x", CultureInfo.InvariantCulture),
            };

            try
            {
                if (!GetModuleHandleExW(FromAddress | UnchangedRefcount, new IntPtr((long) address), out var module)
                    || module == IntPtr.Zero)
                {
                    return frame;
                }

                var relative = address - (ulong) module.ToInt64();
                var buffer = new StringBuilder(1024);
                var length = GetModuleFileNameW(module, buffer, (uint) buffer.Capacity);
                var path = length > 0 ? buffer.ToString(0, (int) length) : "";

                frame["module"] = path.Length > 0 ? Path.GetFileName(path) : "?";
                frame["function"] = "0x" + relative.ToString("x", CultureInfo.InvariantCulture);
                frame["relativeAddr"] = "0x" + relative.ToString("x", CultureInfo.InvariantCulture);

                if (path.Length > 0) frame["image"] = path;

                var debugId = DebugIdOf(module, path);

                if (debugId != null) frame["buildId"] = debugId;
            }
            catch (Exception)
            {
                // A module that will not say: the address alone.
            }

            return frame;
        }

        private static string DebugIdOf(IntPtr module, string path)
        {
            lock (DebugIds)
            {
                if (DebugIds.TryGetValue(module, out var known)) return known;
            }

            string found = null;

            try
            {
                if (path.Length > 0 && File.Exists(path))
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new BinaryReader(stream))
                    {
                        found = DebugId.ReadCodeView(reader);
                    }
                }
            }
            catch (Exception)
            {
            }

            lock (DebugIds) DebugIds[module] = found;

            return found;
        }
    }
}
