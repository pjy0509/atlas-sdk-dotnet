using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
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
        private readonly CrashReporter _reporter;
        private readonly List<string> _installed = new List<string>();

        internal ManagedHooks(CrashReporter reporter)
        {
            _reporter = reporter;
        }

        internal IReadOnlyList<string> Installed
        {
            get { return _installed; }
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

            // WPF: the calling thread's Dispatcher always exists, unlike
            // Application.Current at start time.
            if (HookEvent("WindowsBase", "System.Windows.Threading.Dispatcher", "CurrentDispatcher", "UnhandledException",
                CrashReport.MechanismDispatcher))
            {
                _installed.Add("wpf");
            }

            // WinForms: a static event; Application.ThreadException.
            if (HookEvent("System.Windows.Forms", "System.Windows.Forms.Application", null, "ThreadException",
                CrashReport.MechanismThreadException))
            {
                _installed.Add("winForms");
            }

            // WinUI 3: Application.Current.UnhandledException (XAML dispatch
            // only; the AppDomain backstop covers the rest).
            if (HookEvent("Microsoft.WinUI", "Microsoft.UI.Xaml.Application", "Current", "UnhandledException",
                CrashReport.MechanismDispatcher))
            {
                _installed.Add("winUI");
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

        /// <summary>Attaches to `eventName` on the object `memberName` yields
        /// from `typeName` (a static property, or null for a static event),
        /// when the assembly is loaded at all. The handler's delegate type is
        /// whatever the event declares; it is built with an expression tree so
        /// no framework type is named here.</summary>
        private bool HookEvent(string assemblyName, string typeName, string memberName, string eventName, string mechanism)
        {
            try
            {
                var type = FindType(assemblyName, typeName);

                if (type == null) return false;

                object target = null;

                if (memberName != null)
                {
                    var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
                    target = property?.GetValue(null);

                    if (target == null) return false;
                }

                var evt = type.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

                if (evt == null) return false;

                var invoke = evt.EventHandlerType.GetMethod("Invoke");
                var parameters = invoke.GetParameters();

                if (parameters.Length != 2) return false;

                // (sender, args) => this.OnFrameworkException(args, mechanism)
                var sender = Expression.Parameter(parameters[0].ParameterType, "sender");
                var args = Expression.Parameter(parameters[1].ParameterType, "args");
                var bridge = typeof(ManagedHooks).GetMethod(nameof(OnFrameworkException), BindingFlags.NonPublic | BindingFlags.Instance);
                var body = Expression.Call(Expression.Constant(this), bridge, Expression.Convert(args, typeof(object)),
                    Expression.Constant(mechanism));
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
