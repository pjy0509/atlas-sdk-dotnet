using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// A UI-thread hang, seen from a thread of its own: a callback is posted
    /// to the UI thread (through the SynchronizationContext captured at
    /// start, or the WPF Dispatcher or WinForms form found later), and one
    /// that has not run by the next tick means the UI thread is stuck.
    /// Reported once per freeze, the peer of an Android ANR. Only where a UI
    /// loop exists, since a console or service host has no thread whose
    /// stall a user feels, and never under a debugger. A process that slept
    /// through the tick (standby, hibernation) is not a hung one.
    /// </summary>
    internal sealed class HangWatchdog
    {
        internal const int TimeoutMs = 5000;

        private readonly Action<Action> _post;
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

            return new HangWatchdog(work => context.Post(_ => work(), null), timeoutMs, onHang, onRecover);
        }

        /// <summary>
        /// The main thread's loop, found from another thread once the app has
        /// made one: WPF's Dispatcher for that thread, or the first WinForms
        /// form. Null until then; the caller asks again. For a start that
        /// ran before the app (the startup hook), where no context existed yet.
        /// </summary>
        internal static HangWatchdog ForMainThread(Thread main, Action<long> onHang, Action onRecover, int timeoutMs = TimeoutMs)
        {
            var post = WpfPoster(main) ?? WinFormsPoster();

            return post == null ? null : new HangWatchdog(post, timeoutMs, onHang, onRecover);
        }

        private static Action<Action> WpfPoster(Thread main)
        {
            try
            {
                var type = FindType("WindowsBase", "System.Windows.Threading.Dispatcher");
                var dispatcher = type?.GetMethod("FromThread", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new object[] {main});
                var begin = dispatcher?.GetType().GetMethod("BeginInvoke", new[] {typeof(Delegate), typeof(object[])});

                if (begin == null) return null;

                return work => begin.Invoke(dispatcher, new object[] {(Action) work, new object[0]});
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Action<Action> WinFormsPoster()
        {
            try
            {
                var application = FindType("System.Windows.Forms", "System.Windows.Forms.Application");
                var forms = application?.GetProperty("OpenForms", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    as System.Collections.IEnumerable;
                object form = null;

                if (forms != null)
                {
                    foreach (var candidate in forms)
                    {
                        form = candidate;
                        break;
                    }
                }

                var begin = form?.GetType().GetMethod("BeginInvoke", new[] {typeof(Delegate)});

                if (begin == null) return null;

                return work => begin.Invoke(form, new object[] {(Action) work});
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Type FindType(string assemblyName, string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)) continue;

                var type = assembly.GetType(typeName, false);

                if (type != null) return type;
            }

            return null;
        }

        internal HangWatchdog(SynchronizationContext ui, int timeoutMs, Action<long> onHang, Action onRecover)
            : this(work => ui.Post(_ => work(), null), timeoutMs, onHang, onRecover)
        {
        }

        internal HangWatchdog(Action<Action> post, int timeoutMs, Action<long> onHang, Action onRecover)
        {
            _post = post;
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
                    _post(() => Interlocked.Exchange(ref _lastAck, Stopwatch.GetTimestamp()));
                }
                catch (Exception)
                {
                    // The loop is gone (the window closed): nothing to watch.
                    return;
                }

                Thread.Sleep(_timeoutMs);

                if (_stop) return;

                // Woke far later than asked: the machine slept, not the UI
                // thread. Nothing about this tick is trusted.
                if ((Stopwatch.GetTimestamp() - posted) * 1000L / Stopwatch.Frequency > _timeoutMs * 2L) continue;

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
