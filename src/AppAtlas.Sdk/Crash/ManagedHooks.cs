using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// The managed hooks, four deep (the server repo's docs/sdk-crash.md
    /// §2.3): AppDomain.UnhandledException is the backstop and the one that
    /// is notice-only — the process dies right after it; the others are
    /// framework-specific and found by name so the assembly stays
    /// netstandard2.0 with no framework references. The UI hooks fire before
    /// the app's own handler decides, so they are recorded as errors and the
    /// backstop still writes the crash when nothing handled it.
    /// </summary>
    internal sealed class ManagedHooks
    {
        // How long a framework hook keeps being retried after its assembly
        // loads: WinUI's Application.Current is null until the app constructs it.
        internal const int RetryForMs = 60_000;
        private const int RetryEveryMs = 1000;

        private readonly CrashReporter _reporter;
        private readonly List<string> _installed = new List<string>();
        private readonly object _lock = new object();
        private readonly Thread _main = Thread.CurrentThread;
        private Timer _retry;
        private long _retryUntil;

        /// <summary>One framework hook: where it lives, what it is called, how its exceptions are filed.</summary>
        private sealed class Hook
        {
            internal string Name;
            internal string Assembly;
            internal string Type;
            internal string Member;
            internal string Event;
            internal string Mechanism;
            /// <summary>The instance to attach to, when it is not a static property: the main thread's Dispatcher.</summary>
            internal Func<Type, Thread, object> Target;
        }

        // The frameworks a desktop app dispatches on, each found by name so
        // the assembly stays netstandard2.0 with no framework references.
        private static readonly Hook[] Hooks =
        {
            // WPF: the main thread's Dispatcher, which Application reuses.
            // Asked for by thread, so it is never made on the wrong one; null
            // until the main thread has touched WPF, then retried.
            new Hook {Name = "wpf", Assembly = "WindowsBase", Type = "System.Windows.Threading.Dispatcher",
                Event = "UnhandledException", Mechanism = CrashReport.MechanismDispatcher,
                Target = (type, main) => type.GetMethod("FromThread", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new object[] {main})},
            // WinForms: a static event.
            new Hook {Name = "winForms", Assembly = "System.Windows.Forms", Type = "System.Windows.Forms.Application",
                Member = null, Event = "ThreadException", Mechanism = CrashReport.MechanismThreadException},
            // WinUI 3: XAML dispatch only; the AppDomain backstop covers the rest.
            new Hook {Name = "winUI", Assembly = "Microsoft.WinUI", Type = "Microsoft.UI.Xaml.Application",
                Member = "Current", Event = "UnhandledException", Mechanism = CrashReport.MechanismDispatcher},
            // Avalonia 11: the UI thread's Dispatcher.
            new Hook {Name = "avalonia", Assembly = "Avalonia.Base", Type = "Avalonia.Threading.Dispatcher",
                Member = "UIThread", Event = "UnhandledException", Mechanism = CrashReport.MechanismDispatcher},
        };

        internal ManagedHooks(CrashReporter reporter)
        {
            _reporter = reporter;
        }

        internal IReadOnlyList<string> Installed
        {
            get { lock (_lock) return new List<string>(_installed); }
        }

        internal void Install()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                _installed.Add("appDomain");
            }
            catch (Exception)
            {
                // A host that forbids it: the rest still stand.
            }

            try
            {
                TaskScheduler.UnobservedTaskException += OnUnobserved;
                _installed.Add("taskScheduler");
            }
            catch (Exception)
            {
            }

            HookFrameworks();

            // Started before the app (the startup hook), no framework is
            // loaded yet: each one is hooked as its assembly arrives, and
            // WinUI's Application.Current is retried until the app makes it.
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The frameworks not yet hooked, tried again; true when every one is done.</summary>
        private bool HookFrameworks()
        {
            var pending = false;

            foreach (var hook in Hooks)
            {
                lock (_lock)
                {
                    if (_installed.Contains(hook.Name)) continue;
                }

                var type = FindType(hook.Assembly, hook.Type);

                if (type == null) continue;

                if (HookEvent(hook, type))
                {
                    lock (_lock) _installed.Add(hook.Name);
                }
                else
                {
                    pending = true;
                }
            }

            return !pending;
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                var name = args.LoadedAssembly.GetName().Name;
                var interesting = false;

                foreach (var hook in Hooks)
                {
                    if (string.Equals(name, hook.Assembly, StringComparison.OrdinalIgnoreCase)) interesting = true;
                }

                if (!interesting) return;

                if (!HookFrameworks()) RetrySoon();
            }
            catch (Exception)
            {
                // A hook that cannot be placed now is tried again later.
            }
        }

        private void RetrySoon()
        {
            lock (_lock)
            {
                _retryUntil = Environment.TickCount + RetryForMs;

                if (_retry != null) return;

                _retry = new Timer(_ =>
                {
                    var done = HookFrameworks();

                    if (done || Environment.TickCount - _retryUntil > 0)
                    {
                        lock (_lock)
                        {
                            _retry?.Dispose();
                            _retry = null;
                        }
                    }
                }, null, RetryEveryMs, RetryEveryMs);
            }
        }

        // The corrupted-state attributes: on .NET Framework an access
        // violation reaches no handler without them. Obsolete on .NET 6+, where
        // such faults end the process regardless and WER's dump is the record.
#pragma warning disable SYSLIB0032
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
#pragma warning restore SYSLIB0032
        private void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
        {
            var error = args.ExceptionObject as Exception
                        ?? new Exception("Non-exception object thrown: " + (args.ExceptionObject?.GetType().FullName ?? "null"));

            _reporter.Crash(error, args.IsTerminating);
        }

        private void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs args)
        {
            // Fires at GC time, for a task nobody awaited; the process goes
            // on. Never flushed, never marked fatal. QUIC's are noise on
            // .NET 7+ (docs/sdk-cautions.md §3).
            var error = args.Exception?.InnerException ?? (Exception) args.Exception;

            if (error == null || error.GetType().FullName == "System.Net.Quic.QuicException") return;

            _reporter.Error(error, CrashReport.MechanismUnobserved);
        }

        /// <summary>Attaches to the hook's event on the object its member or
        /// target yields (a static property, a resolver, or null for a static
        /// event). The handler's delegate type is whatever the event declares;
        /// it is built with an expression tree so no framework type is named
        /// here. False when the target does not exist yet.</summary>
        private bool HookEvent(Hook hook, Type type)
        {
            try
            {
                object target = null;

                if (hook.Target != null)
                {
                    target = hook.Target(type, _main);

                    if (target == null) return false;
                }
                else if (hook.Member != null)
                {
                    var property = type.GetProperty(hook.Member, BindingFlags.Public | BindingFlags.Static);
                    target = property?.GetValue(null);

                    if (target == null) return false;
                }

                var evt = (target?.GetType() ?? type).GetEvent(hook.Event,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

                if (evt == null) return false;

                var invoke = evt.EventHandlerType.GetMethod("Invoke");
                var parameters = invoke.GetParameters();

                if (parameters.Length != 2) return false;

                // (sender, args) => this.OnFrameworkException(args, mechanism)
                var sender = Expression.Parameter(parameters[0].ParameterType, "sender");
                var args = Expression.Parameter(parameters[1].ParameterType, "args");
                var bridge = typeof(ManagedHooks).GetMethod(nameof(OnFrameworkException), BindingFlags.NonPublic | BindingFlags.Instance);
                var body = Expression.Call(Expression.Constant(this), bridge, Expression.Convert(args, typeof(object)),
                    Expression.Constant(hook.Mechanism));
                var handler = Expression.Lambda(evt.EventHandlerType, body, sender, args).Compile();

                evt.AddEventHandler(target, handler);

                return true;
            }
            catch (Exception)
            {
                // Not that framework, or a host without dynamic code.
                return false;
            }
        }

        private void OnFrameworkException(object args, string mechanism)
        {
            try
            {
                var error = args?.GetType().GetProperty("Exception")?.GetValue(args) as Exception;

                if (error != null) _reporter.Error(error, mechanism);
            }
            catch (Exception)
            {
                // The args are not the shape the framework documents: skip.
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
    }
}
