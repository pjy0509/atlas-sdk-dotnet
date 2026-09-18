using System;
using System.Collections.Generic;
using System.Threading;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// The platform-free half of the crash module: one session per process,
    /// handled errors, the crash the backstop hands over, and the deaths
    /// settled at the next start. A crash is written to disk on the dying
    /// thread and flushed with a short budget — the network is not trusted
    /// at the moment of a crash, and the disk write is what survives.
    /// </summary>
    internal sealed class CrashReporter
    {
        // The backstop is notice-only: the process ends when it returns. Two
        // seconds is what a terminating process can afford (docs/sdk-cautions.md §3).
        internal const int TerminatingFlushMs = 2000;
        // A crash this soon after start is a launch crash: the next start
        // sends it before anything else can crash the same way.
        internal const long LaunchWindowMs = 5000;
        internal const int LaunchFlushMs = 2000;

        private readonly AtlasCore _core;
        private readonly CrashScope _scope;
        private readonly string _dataDir;
        private readonly RunState _run;
        private readonly object _lock = new object();
        private int _errors;
        private bool _crashed;

        internal string SessionId { get; }
        internal long StartedAtMs { get; }
        internal string StartedAtIso { get; }
        internal bool Enabled { get; set; } = true;
        internal bool CrashedLastRun { get; set; }

        internal CrashReporter(AtlasCore core, CrashScope scope, string dataDir, RunState run, string sessionId,
            long startedAtMs)
        {
            _core = core;
            _scope = scope;
            _dataDir = dataDir;
            _run = run;
            SessionId = sessionId;
            StartedAtMs = startedAtMs;
            StartedAtIso = AtlasCore.Iso(startedAtMs);
        }

        /// <summary>Opens the session. `previousTimeToCrashMs` is how far into
        /// a previous run it died, or negative: a launch crash makes this
        /// start flush before anything else happens.</summary>
        internal void Install(long previousTimeToCrashMs)
        {
            if (!Enabled) return;

            _core.Enqueue("session", SessionItems.Started(AtlasCore.NewEventId(), SessionId, StartedAtIso));

            if (previousTimeToCrashMs >= 0 && previousTimeToCrashMs < LaunchWindowMs)
            {
                _core.FlushWithin(LaunchFlushMs - (int) (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - StartedAtMs));
            }
        }

        // --- this run's own -------------------------------------------------------

        /// <summary>The backstop's exception: written now, on this thread,
        /// then flushed for as long as a dying process can wait.</summary>
        internal void Crash(Exception error, bool terminating)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            try
            {
                lock (_lock)
                {
                    // One fatal per session: a second thread dying behind
                    // the first adds nothing.
                    if (!Enabled || _crashed) return;

                    _crashed = true;
                }

                Dictionary<string, object> report;

                try
                {
                    report = CrashReport.Payload(AtlasCore.NewEventId(), AtlasCore.Iso(now), SessionId,
                        CrashReport.MechanismUncaught, false, error, true);
                    _scope.WriteTo(report);
                    report["context"] = Context(now);
                }
                catch (Exception)
                {
                    // The full report did not fit in what is left: the
                    // exception alone still names the file and the line.
                    report = CrashReport.Payload(AtlasCore.NewEventId(), AtlasCore.Iso(now), SessionId,
                        CrashReport.MechanismUncaught, false, error, false);
                }

                // Together, so crash-free never sees a crash without its session's end.
                _core.Batch()
                    .Add("crash", report)
                    .Add("session", SessionItems.Ended(AtlasCore.NewEventId(), SessionId, "crashed", StartedAtIso,
                        _errors, now - StartedAtMs))
                    .PersistNow();

                _run.Set("crashWritten", true);
                _run.Set("crashedAt", now);
                _run.Persist();

                if (terminating) _core.FlushWithin(TerminatingFlushMs);
            }
            catch (Exception)
            {
                // A reporter that fails must still let the app die its own death.
            }
        }

        /// <summary>A handled error, or a framework hook's exception before
        /// the app decided: reported, grouped apart from crashes, never fatal.</summary>
        internal void Error(Exception error, string mechanism)
        {
            if (error == null || !Enabled) return;

            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var report = CrashReport.Payload(AtlasCore.NewEventId(), AtlasCore.Iso(now), SessionId, mechanism, true,
                    error, false);

                // A recorded error with no stack of its own (never thrown) is
                // placed where it was recorded.
                var root = (Dictionary<string, object>) ((List<object>) report["exceptions"])[((List<object>) report["exceptions"]).Count - 1];

                if (((List<object>) root["frames"]).Count == 0 && mechanism == CrashReport.MechanismRecorded)
                {
                    root["frames"] = CrashReport.Frames(new System.Diagnostics.StackTrace(2, true));
                }

                _scope.WriteTo(report);
                report["context"] = Context(now);

                var batch = _core.Batch().Add("error", report);

                if (Interlocked.Increment(ref _errors) == 1)
                {
                    batch.Add("session", SessionItems.Errored(AtlasCore.NewEventId(), SessionId, StartedAtIso));
                }

                batch.Enqueue();
            }
            catch (Exception)
            {
                // Recording must never take the process with it.
            }
        }

        /// <summary>The watchdog's finding: the UI thread stopped answering.
        /// Reported as `anr`; the session's fate is settled at the next
        /// start, when the run state says whether it ever recovered.</summary>
        internal void Hang(long stuckMs)
        {
            if (!Enabled) return;

            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var report = CrashReport.FromExit(AtlasCore.NewEventId(), AtlasCore.Iso(now), SessionId,
                    CrashReport.MechanismAnr, "AppHang",
                    "The UI thread did not answer for " + stuckMs + "ms", null);
                _scope.WriteTo(report);
                report["context"] = Context(now);
                _core.Enqueue("crash", report);
            }
            catch (Exception)
            {
            }
        }

        // --- the previous runs' deaths ---------------------------------------------

        /// <summary>Settles every instance that died since the last start:
        /// a managed crash it wrote itself (its session end went with it),
        /// a native death WER dumped, or an exit nothing explained.</summary>
        internal void SettleDeadRuns(List<Dictionary<string, object>> deadRuns, string dumpDirectory)
        {
            foreach (var dead in deadRuns)
            {
                var sessionId = dead["sessionId"] as string;
                var startedAt = dead.TryGetValue("startedAt", out var started) && started is long s ? s : 0L;
                var endedAt = dead.TryGetValue("endedAt", out var ended) && ended is long e ? e : startedAt;
                var crashWritten = dead.TryGetValue("crashWritten", out var written) && written is bool w && w;
                var cleanExit = dead.TryGetValue("cleanExit", out var clean) && clean is bool c && c;
                var hanging = dead.TryGetValue("hanging", out var hang) && hang is bool h && h;

                if (crashWritten)
                {
                    // The crash and its session end left at crash time.
                    CrashedLastRun = true;
                }

                var dumps = WerDumps.Take(dumpDirectory, startedAt, crashWritten);

                foreach (var dump in dumps)
                {
                    CrashedLastRun = true;

                    if (!Enabled) continue;

                    var at = dump["writtenAt"] is long writtenAt ? writtenAt : endedAt;
                    var report = CrashReport.FromExit(AtlasCore.NewEventId(), AtlasCore.Iso(at), sessionId,
                        CrashReport.MechanismExitInfo, (string) dump["type"], (string) dump["message"],
                        (List<object>) dump["frames"]);
                    ((Dictionary<string, object>) report["mechanism"])["native"] = new Dictionary<string, object>
                    {
                        ["exceptionCode"] = dump["code"],
                        ["faultAddress"] = dump["address"],
                        ["threadId"] = dump["threadId"],
                        ["source"] = "WER LocalDumps",
                    };
                    report["context"] = new Dictionary<string, object>
                    {
                        ["startedAt"] = AtlasCore.Iso(startedAt),
                        ["timeToCrashMs"] = Math.Max(at - startedAt, 0),
                    };

                    var batch = _core.Batch().Add("crash", report);

                    if (sessionId != null && !crashWritten)
                    {
                        batch.Add("session", SessionItems.Ended(AtlasCore.NewEventId(), sessionId, "crashed",
                            AtlasCore.Iso(startedAt), 0, at - startedAt));
                    }

                    batch.Enqueue();
                }

                if (crashWritten || dumps.Count > 0 || cleanExit || sessionId == null || !Enabled) continue;

                // No report, no dump, no clean exit: a kill, a stack overflow
                // WER did not catch, a power cut. Not an issue — nothing to
                // group on — but not a clean session either.
                CrashedLastRun |= hanging;
                _core.Enqueue("session", SessionItems.Ended(AtlasCore.NewEventId(), sessionId, "abnormal",
                    AtlasCore.Iso(startedAt), 0, endedAt - startedAt));
            }
        }

        internal Dictionary<string, object> Context(long nowMs)
        {
            var context = new Dictionary<string, object>
            {
                ["startedAt"] = StartedAtIso,
                ["timeToCrashMs"] = nowMs - StartedAtMs,
            };

            try
            {
                foreach (var fact in DeviceFacts.Now(_dataDir)) context[fact.Key] = fact.Value;
            }
            catch (Exception)
            {
                // The facts are a bonus; the report goes without them.
            }

            return context;
        }
    }
}
