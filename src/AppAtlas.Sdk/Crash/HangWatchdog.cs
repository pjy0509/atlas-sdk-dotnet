using System;
using System.Diagnostics;
using System.Threading;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// A UI-thread hang, seen from a thread of its own: a callback is posted
    /// through the UI SynchronizationContext captured at start (WPF's
    /// Dispatcher, WinForms', WinUI's DispatcherQueue), and one that has not
    /// run by the next tick means the UI thread is stuck. Reported once per
    /// freeze, the peer of an Android ANR. Only where a UI context exists —
    /// a console or service host has no thread whose stall a user feels —
    /// and never under a debugger.
    /// </summary>
    internal sealed class HangWatchdog
    {
        internal const int TimeoutMs = 5000;

        private readonly SynchronizationContext _ui;
        private readonly int _timeoutMs;
        private readonly Action<long> _onHang;
        private readonly Action _onRecover;
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _lastAck;

        /// <summary>Null when the calling thread has no UI context to watch.</summary>
        internal static HangWatchdog ForCurrentThread(Action<long> onHang, Action onRecover, int timeoutMs = TimeoutMs)
        {
            var context = SynchronizationContext.Current;

            if (context == null) return null;

            var name = context.GetType().FullName ?? "";

            // The three contexts that pump a message loop; the default one
            // runs callbacks on the pool and would never look stuck.
            if (name.IndexOf("Dispatcher", StringComparison.Ordinal) < 0
                && name.IndexOf("WindowsForms", StringComparison.Ordinal) < 0
                && name.IndexOf("WinUI", StringComparison.Ordinal) < 0)
            {
                return null;
            }

            return new HangWatchdog(context, timeoutMs, onHang, onRecover);
        }

        internal HangWatchdog(SynchronizationContext ui, int timeoutMs, Action<long> onHang, Action onRecover)
        {
            _ui = ui;
            _timeoutMs = timeoutMs;
            _onHang = onHang;
            _onRecover = onRecover;
            _thread = new Thread(Run) {IsBackground = true, Name = "atlas-crash-watchdog"};
        }

        internal void Start()
        {
            _lastAck = Stopwatch.GetTimestamp();
            _thread.Start();
        }

        internal void Stop()
        {
            _stop = true;
        }

        private void Run()
        {
            var firedForThisFreeze = false;
            long stuckSince = 0;

            while (!_stop)
            {
                var posted = Stopwatch.GetTimestamp();

                try
                {
                    _ui.Post(_ => Interlocked.Exchange(ref _lastAck, Stopwatch.GetTimestamp()), null);
                }
                catch (Exception)
                {
                    // The loop is gone (the window closed): nothing to watch.
                    return;
                }

                Thread.Sleep(_timeoutMs);

                if (_stop) return;

                // Answered within the window: no freeze, and any earlier
                // freeze has cleared, so the next one may fire again.
                if (Interlocked.Read(ref _lastAck) >= posted)
                {
                    if (firedForThisFreeze) _onRecover?.Invoke();

                    firedForThisFreeze = false;
                    stuckSince = 0;
                    continue;
                }

                // A debugger paused the thread: not a freeze the user feels.
                if (Debugger.IsAttached) continue;

                if (stuckSince == 0) stuckSince = posted;

                if (!firedForThisFreeze)
                {
                    firedForThisFreeze = true;
                    var stuckMs = (Stopwatch.GetTimestamp() - stuckSince) * 1000L / Stopwatch.Frequency;
                    _onHang(stuckMs);
                }
            }
        }
    }
}
