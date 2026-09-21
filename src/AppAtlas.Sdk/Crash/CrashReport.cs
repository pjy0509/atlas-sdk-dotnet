using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// An exception as the `crash` / `error` item the server groups on (the
    /// server repo's docs/sdk-crash.md §3). The exception already holds its
    /// stack, so nothing here unwinds anything. Frames carry the platform
    /// neutral vocabulary plus what a portable PDB needs to resolve them on
    /// the server: the method token, the IL offset and the module's debug id.
    ///
    /// FROZEN once shipped: the mechanism strings and the async demangling
    /// regexes. The server keys issue kinds on the former, fingerprints on
    /// the latter, and a change regroups every open issue.
    /// </summary>
    internal static class CrashReport
    {
        internal const string MechanismUncaught = "uncaughtExceptionHandler";
        internal const string MechanismRecorded = "recordError";
        internal const string MechanismUnobserved = "unobservedTaskException";
        internal const string MechanismDispatcher = "dispatcherUnhandledException";
        internal const string MechanismThreadException = "threadException";
        internal const string MechanismAnr = "anr";
        // A death the OS recorded and this SDK only heard about at the next start.
        internal const string MechanismExitInfo = "exitInfo";

        internal const int MaxCauses = 8;
        internal const int MaxFrames = 256;

        // The compiler's names for what the source calls something else:
        // `<Method>d__12` state machines, `<Method>b__0_1` lambdas,
        // `<Method>g__Local|0_0` local functions, `<>c__DisplayClass2_0` closures.
        private static readonly Regex StateMachine = new Regex(@"^<([^>]+)>d__\d+$", RegexOptions.Compiled);
        private static readonly Regex Lambda = new Regex(@"^<([^>]+)>b__[\d_]+$", RegexOptions.Compiled);
        private static readonly Regex LocalFunction = new Regex(@"^<([^>]+)>g__([^|]+)\|[\d_]+$", RegexOptions.Compiled);
        private static readonly Regex Closure = new Regex(@"\+<>c(__DisplayClass[\d_]+)?$", RegexOptions.Compiled);

        internal static Dictionary<string, object> Payload(string eventId, string crashedAtIso, string sessionId,
            string mechanism, bool handled, Exception error, bool withThread)
        {
            var payload = Head(eventId, crashedAtIso, sessionId, mechanism, handled);
            var exceptions = Exceptions(error);
            payload["exceptions"] = exceptions;

            var detail = Detail(error);

            if (detail != null) ((Dictionary<string, object>) payload["mechanism"])["data"] = detail;

            if (withThread)
            {
                var thread = Thread.CurrentThread;
                var root = (Dictionary<string, object>) exceptions[exceptions.Count - 1];
                payload["threads"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = string.IsNullOrEmpty(thread.Name)
                            ? "thread-" + thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture)
                            : thread.Name,
                        ["crashed"] = true,
                        ["frames"] = root["frames"],
                    },
                };
            }

            return payload;
        }

        /// <summary>A death with no exception object: a hang, a native fault
        /// the OS wrote a dump for, an exit nothing explained. The type is
        /// what groups it; `frames` may be empty.</summary>
        internal static Dictionary<string, object> FromExit(string eventId, string crashedAtIso, string sessionId,
            string mechanism, string type, string message, List<object> frames)
        {
            var payload = Head(eventId, crashedAtIso, sessionId, mechanism, false);
            payload["exceptions"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = type,
                    ["message"] = message ?? "",
                    ["frames"] = frames ?? new List<object>(),
                },
            };

            return payload;
        }

        private static Dictionary<string, object> Head(string eventId, string crashedAtIso, string sessionId,
            string mechanism, bool handled)
        {
            var payload = new Dictionary<string, object>
            {
                ["eventId"] = eventId,
                ["crashedAt"] = crashedAtIso,
            };

            if (sessionId != null) payload["sessionId"] = sessionId;

            payload["mechanism"] = new Dictionary<string, object> {["type"] = mechanism, ["handled"] = handled};

            return payload;
        }

        /// <summary>Outermost first, down InnerException; the last entry is
        /// the root cause. An AggregateException's first inner is its
        /// InnerException already; the others follow it, so a Task.WhenAll
        /// that failed twice shows both.</summary>
        private static List<object> Exceptions(Exception error)
        {
            var chain = new List<object>();
            var seen = new HashSet<Exception>();

            for (var link = error; link != null && chain.Count < MaxCauses && seen.Add(link); link = link.InnerException)
            {
                chain.Add(Raised(link));
            }

            var aggregate = error as AggregateException;

            if (aggregate != null)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    for (var link = inner; link != null && chain.Count < MaxCauses && seen.Add(link); link = link.InnerException)
                    {
                        chain.Add(Raised(link));
                    }
                }
            }

            return chain;
        }

        private static Dictionary<string, object> Raised(Exception link)
        {
            return new Dictionary<string, object>
            {
                ["type"] = link.GetType().FullName ?? "Exception",
                ["message"] = Message(link),
                ["frames"] = Frames(link),
            };
        }

        /// <summary>What the exception carries beside its message: the HRESULT
        /// (the Win32 or COM error behind an IOException, say) and the
        /// string entries of Exception.Data. Null when there is nothing.</summary>
        private static Dictionary<string, object> Detail(Exception error)
        {
            var detail = new Dictionary<string, object>();

            try
            {
                var hresult = error.HResult;

                // Every type carries a default HRESULT that says nothing the
                // type does not. The ones that carry a real code are the
                // interop and I/O families: a Win32 or COM error behind an
                // IOException, a COMException, a Win32Exception.
                var carries = error is System.IO.IOException || error is System.Runtime.InteropServices.ExternalException;

                if (carries && hresult != 0 && (hresult & unchecked((int) 0xFFFF0000)) != unchecked((int) 0x80130000))
                {
                    detail["hresult"] = "0x" + hresult.ToString("x8", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                var data = error.Data;

                if (data != null && data.Count > 0)
                {
                    var entries = new Dictionary<string, object>();

                    foreach (System.Collections.DictionaryEntry entry in data)
                    {
                        if (entries.Count >= 32) break;

                        var key = entry.Key as string;

                        if (key == null) continue;

                        var value = entry.Value?.ToString() ?? "";
                        entries[key.Length > 64 ? key.Substring(0, 64) : key] = value.Length > 1024 ? value.Substring(0, 1024) : value;
                    }

                    if (entries.Count > 0) detail["data"] = entries;
                }
            }
            catch (Exception)
            {
            }

            return detail.Count > 0 ? detail : null;
        }

        private static string Message(Exception link)
        {
            try
            {
                var message = link.Message ?? "";

                return message.Length > 1000 ? message.Substring(0, 1000) : message;
            }
            catch (Exception)
            {
                // Message is app code, and this runs while the app is dying.
                return "";
            }
        }

        internal static List<object> Frames(Exception error)
        {
            try
            {
                return Frames(new StackTrace(error, true));
            }
            catch (Exception)
            {
                return new List<object>();
            }
        }

        internal static List<object> Frames(StackTrace trace)
        {
            var frames = new List<object>();
            var raw = trace.GetFrames();

            for (var i = 0; raw != null && i < raw.Length && frames.Count < MaxFrames; i++)
            {
                var frame = Frame(raw[i]);

                if (frame != null) frames.Add(frame);
            }

            return frames;
        }

        private static Dictionary<string, object> Frame(StackFrame raw)
        {
            MethodBase method;

            try
            {
                method = raw.GetMethod();
            }
            catch (Exception)
            {
                method = null;
            }

            var frame = new Dictionary<string, object>();
            var typeName = method?.DeclaringType?.FullName;
            var methodName = method?.Name;
            Demangle(ref typeName, ref methodName);

            frame["module"] = typeName ?? "?";
            frame["function"] = methodName ?? "?";

            var file = Probe(() => raw.GetFileName());
            var line = Probe(() => raw.GetFileLineNumber(), 0);

            if (!string.IsNullOrEmpty(file)) frame["file"] = file;
            if (line > 0) frame["line"] = line;

            if (method != null)
            {
                // What a portable PDB resolves a frame by, when no line came
                // with the binary.
                var token = Probe(() => method.MetadataToken, 0);
                var il = Probe(() => raw.GetILOffset(), StackFrame.OFFSET_UNKNOWN);
                var module = Probe(() => method.Module);

                if (token != 0) frame["methodToken"] = "0x" + token.ToString("x8", CultureInfo.InvariantCulture);
                if (il != StackFrame.OFFSET_UNKNOWN) frame["ilOffset"] = il;

                var debugId = DebugId.Of(module);

                if (debugId != null) frame["debugId"] = debugId;

                var assembly = Probe(() => module?.Assembly?.GetName()?.Name);

                if (!string.IsNullOrEmpty(assembly)) frame["assembly"] = assembly;
            }

            return frame;
        }

        /// <summary>The source's names back from the compiler's.</summary>
        internal static void Demangle(ref string typeName, ref string methodName)
        {
            if (typeName == null || methodName == null) return;

            var plus = typeName.LastIndexOf('+');
            var nested = plus >= 0 ? typeName.Substring(plus + 1) : null;
            Match match;

            // async/iterator: Outer+<Method>d__3.MoveNext → Outer.Method
            if (nested != null && methodName == "MoveNext" && (match = StateMachine.Match(nested)).Success)
            {
                typeName = typeName.Substring(0, plus);
                methodName = match.Groups[1].Value;

                return;
            }

            // lambda: Outer+<>c.<Method>b__0_1 → Outer.Method.<lambda>
            if ((match = Lambda.Match(methodName)).Success)
            {
                typeName = Closure.Replace(typeName, "");
                methodName = match.Groups[1].Value + ".<lambda>";

                return;
            }

            // local function: Outer.<Method>g__Local|0_0 → Outer.Method.Local
            if ((match = LocalFunction.Match(methodName)).Success)
            {
                typeName = Closure.Replace(typeName, "");
                methodName = match.Groups[1].Value + "." + match.Groups[2].Value;
            }
        }

        private static T Probe<T>(Func<T> read, T fallback = default(T))
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
