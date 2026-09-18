using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// The crash module: unhandled exceptions from every hook the host has
    /// (AppDomain, unobserved tasks, WPF, WinForms, WinUI), native deaths
    /// through the dumps Windows Error Reporting writes for this exe, UI
    /// thread hangs, and the deaths nothing recorded — all on their own;
    /// handled errors and context when the app offers them. Nothing to wire
    /// beyond Atlas.Start:
    ///
    ///     Atlas.Start("sdk_…");
    ///     AtlasCrash.SetUserId("u-123");
    ///     AtlasCrash.SetKey("screen", "checkout");
    ///     AtlasCrash.LeaveBreadcrumb("cart", "add");
    ///     AtlasCrash.Log("cart total recomputed");
    ///     AtlasCrash.RecordError(error);
    /// </summary>
    public static class AtlasCrash
    {
        private const string EnabledFile = "crash-enabled";

        private static readonly object Lock = new object();
        // Context set before Start is kept: an app may name its user first.
        private static readonly CrashScope Scope = new CrashScope();
        private static CrashReporter _reporter;
        private static RunState _run;
        private static ManagedHooks _hooks;
        private static HangWatchdog _watchdog;
        private static string _dataDir;
        private static bool _werRegistered;

        /// <summary>Called by Atlas.Start; not application API.</summary>
        internal static void Boot()
        {
            var core = Atlas.Core;
            var dataDir = Atlas.DataDir;

            lock (Lock)
            {
                if (_reporter != null || core == null || dataDir == null) return;

                _dataDir = Path.Combine(dataDir, "crash");
                _run = new RunState(Path.Combine(_dataDir, "runs"), AtlasCore.NewEventId(),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _reporter = new CrashReporter(core, Scope, _dataDir, _run, _run.SessionId, _run.StartedAtMs);
                _reporter.Enabled = ReadEnabled();

                if (!_reporter.Enabled)
                {
                    // Off: nothing collected, and nothing a re-enable could send late.
                    _run.TakeDeadRuns();

                    return;
                }
            }

            var dumpDir = Path.Combine(_dataDir, "dumps");
            var dead = _run.TakeDeadRuns();

            // A launch crash in the run just settled makes this start flush first.
            long previousTimeToCrash = -1;

            foreach (var gone in dead)
            {
                if (gone.TryGetValue("crashedAt", out var at) && at is long crashedAt
                    && gone.TryGetValue("startedAt", out var started) && started is long startedAt)
                {
                    previousTimeToCrash = crashedAt - startedAt;
                }

                // Known now, not after the settle thread: the app asks early.
                if (gone.TryGetValue("crashWritten", out var written) && written is bool w && w)
                {
                    _reporter.CrashedLastRun = true;
                }
            }

            _reporter.Install(previousTimeToCrash);

            _hooks = new ManagedHooks(_reporter);
            _hooks.Install();
            _werRegistered = WerDumps.Register(dumpDir);

            try
            {
                AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
                {
                    _run.Set("cleanExit", true);
                    _run.Persist();
                };
            }
            catch (Exception)
            {
            }

            _watchdog = HangWatchdog.ForCurrentThread(stuckMs =>
            {
                _run.Set("hanging", true);
                _run.Persist();
                _reporter.Hang(stuckMs);
            }, () =>
            {
                _run.Set("hanging", false);
                _run.Persist();
            });
            _watchdog?.Start();

            // The dead instances' fates, off the caller's thread.
            var settle = new Thread(() =>
            {
                try
                {
                    _reporter.SettleDeadRuns(dead, dumpDir);
                }
                catch (Exception)
                {
                    // A previous run's leftovers must not cost this one.
                }

                _run.Persist();
            }) {IsBackground = true, Name = "atlas-crash-exits"};
            settle.Start();
        }

        /// <summary>Which hooks took, for the gate and for support: appDomain,
        /// taskScheduler, wpf, winForms, winUI, wer, watchdog.</summary>
        internal static IReadOnlyList<string> Installed
        {
            get
            {
                var all = new List<string>(_hooks?.Installed ?? new List<string>());

                if (_werRegistered) all.Add("wer");
                if (_watchdog != null) all.Add("watchdog");

                return all;
            }
        }

        /// <summary>Your own id for the signed-in user; null clears it. Never required.</summary>
        public static void SetUserId(string id)
        {
            Scope.SetUserId(id);
        }

        /// <summary>Up to 64 keys ride every report; a null value removes the key.</summary>
        public static void SetKey(string name, string value)
        {
            Scope.SetKey(name, value);
        }

        /// <summary>The last 100 are kept and attached to the next report.</summary>
        public static void LeaveBreadcrumb(string category, string message)
        {
            Scope.LeaveBreadcrumb(category, message, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>A line in the rolling log: the newest 64 KB ride the next report.</summary>
        public static void Log(string line)
        {
            Scope.Log(line, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>A caught exception worth knowing about; grouped apart
        /// from crashes, never fatal. Quiet before Atlas.Start.</summary>
        public static void RecordError(Exception error)
        {
            _reporter?.Error(error, CrashReport.MechanismRecorded);
        }

        /// <summary>Consent: off, nothing is collected or sent, and the choice
        /// outlives the process. On by default. Takes effect at once for
        /// reports; sessions and the hooks follow from the next start.</summary>
        public static void SetEnabled(bool enabled)
        {
            var reporter = _reporter;

            if (reporter != null) reporter.Enabled = enabled;

            WriteEnabled(enabled);
        }

        /// <summary>Whether a previous run ended in a crash this SDK recorded —
        /// its own, or the dump the OS left.</summary>
        public static bool CrashedLastRun
        {
            get { return _reporter != null && _reporter.CrashedLastRun; }
        }

        private static bool ReadEnabled()
        {
            try
            {
                var path = Path.Combine(_dataDir, EnabledFile);

                return !File.Exists(path) || File.ReadAllText(path).Trim() != "0";
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void WriteEnabled(bool enabled)
        {
            try
            {
                var dir = _dataDir ?? Path.Combine(Atlas.DataDir ?? Path.GetTempPath(), "crash");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, EnabledFile), enabled ? "1" : "0");
            }
            catch (Exception)
            {
                // The choice holds for this process at least.
            }
        }
    }
}
