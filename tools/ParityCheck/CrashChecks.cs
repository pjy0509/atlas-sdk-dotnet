using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AppAtlas.Sdk;
using AppAtlas.Sdk.Crash;

/// <summary>
/// The crash half of the gate: the report shapes, the scope's caps, the
/// debug ids and the minidump reader in-process; then the process itself,
/// spawned as a victim that dies each way the hooks claim to catch, and a
/// boot over what each victim left, read back from the queue it filled
/// against a server that is not there.
/// </summary>
internal static class CrashChecks
{
    // --- the victim ------------------------------------------------------------------

    internal static int Victim(string mode, string dataDir, string baseUrl)
    {
        Atlas.Start("sdk_gate", baseUrl, dataDir);
        AtlasCrash.SetUserId("u-gate");
        AtlasCrash.SetKey("mode", mode);
        AtlasCrash.LeaveBreadcrumb("gate", "about to die");
        AtlasCrash.Log("the last line");
        Atlas.Core.AwaitIdle(5_000);

        switch (mode)
        {
            case "throw":
                ThrowDeep(3);
                break;
            case "thread":
                var worker = new Thread(() => ThrowDeep(2)) {Name = "gate-worker"};
                worker.Start();
                worker.Join();
                break;
            case "async":
                // An exception thrown on an async path, unobserved until the
                // wait: the demangled frames name the async method.
                DieAsync().GetAwaiter().GetResult();
                break;
            case "record":
                try
                {
                    ThrowDeep(1);
                }
                catch (Exception error)
                {
                    AtlasCrash.RecordError(error);
                }

                Atlas.Core.AwaitIdle(5_000);

                return 0;
            case "clean":
                return 0;
            case "kill":
                // No handler, no exit event: what a task-kill or a stack
                // overflow leaves behind.
                Process.GetCurrentProcess().Kill();
                break;
        }

        Console.Error.WriteLine("victim: still alive after " + mode);

        return 3;
    }

    private static void ThrowDeep(int depth)
    {
        if (depth == 0)
        {
            throw new InvalidOperationException("raised on purpose",
                new ArgumentNullException("price", "price was null"));
        }

        ThrowDeep(depth - 1);
    }

    private static async Task DieAsync()
    {
        await Task.Delay(10);
        ThrowDeep(1);
    }

    // --- in-process ----------------------------------------------------------------------

    internal static void CheckReports()
    {
        Exception caught = null;

        try
        {
            ThrowDeep(2);
        }
        catch (Exception error)
        {
            caught = error;
        }

        var payload = Payload(caught, CrashReport("MechanismUncaught"), false, true);
        var exceptions = (List<object>) payload["exceptions"];
        Program.Require(exceptions.Count == 2, "report: the cause chain must be walked");

        var outer = (Dictionary<string, object>) exceptions[0];
        var root = (Dictionary<string, object>) exceptions[1];
        Program.Require((string) outer["type"] == "System.InvalidOperationException", "report: outer type");
        Program.Require((string) root["type"] == "System.ArgumentNullException", "report: root type");
        Program.Require(((string) root["message"]).Contains("price was null"), "report: root message");

        var frames = (List<object>) outer["frames"];
        Program.Require(frames.Count >= 3, "report: the throw site and its callers");
        var top = (Dictionary<string, object>) frames[0];
        Program.Require((string) top["module"] == "CrashChecks" && (string) top["function"] == "ThrowDeep",
            "report: frame 0 is " + top["module"] + "." + top["function"]);
        Program.Require(top.ContainsKey("methodToken") && top.ContainsKey("ilOffset"),
            "report: a frame must carry its method token and IL offset for the PDB");
        Program.Require(top.ContainsKey("debugId"), "report: a frame must carry its module's debug id");
        Program.Require(top.ContainsKey("line") && (int) top["line"] > 0, "report: the line is in the PDB beside the gate");

        var threads = (List<object>) payload["threads"];
        Program.Require(threads.Count == 1 && (bool) ((Dictionary<string, object>) threads[0])["crashed"],
            "report: the dying thread is flagged");

        var mechanism = (Dictionary<string, object>) payload["mechanism"];
        Program.Require((string) mechanism["type"] == "uncaughtExceptionHandler" && !(bool) mechanism["handled"],
            "report: mechanism");

        // The compiler's names, back to the source's.
        var type = "Shop.Cart+<CheckoutAsync>d__12";
        var method = "MoveNext";
        Demangle(ref type, ref method);
        Program.Require(type == "Shop.Cart" && method == "CheckoutAsync", "demangle: async state machine");

        type = "Shop.Cart+<>c__DisplayClass4_0";
        method = "<Total>b__0";
        Demangle(ref type, ref method);
        Program.Require(type == "Shop.Cart" && method == "Total.<lambda>", "demangle: lambda " + type + "." + method);

        type = "Shop.Cart";
        method = "<Total>g__Sum|0_0";
        Demangle(ref type, ref method);
        Program.Require(type == "Shop.Cart" && method == "Total.Sum", "demangle: local function");

        Console.WriteLine("report: the cause chain, the frames' PDB keys and the async names hold");
    }

    internal static void CheckScope()
    {
        var scope = Internal("CrashScope");
        var setKey = scope.GetType().GetMethod("SetKey", BindingFlags.NonPublic | BindingFlags.Instance);
        var crumb = scope.GetType().GetMethod("LeaveBreadcrumb", BindingFlags.NonPublic | BindingFlags.Instance);
        var log = scope.GetType().GetMethod("Log", BindingFlags.NonPublic | BindingFlags.Instance);
        var write = scope.GetType().GetMethod("WriteTo", BindingFlags.NonPublic | BindingFlags.Instance);

        for (var i = 0; i < 80; i++) setKey.Invoke(scope, new object[] {"k" + i, "v"});
        for (var i = 0; i < 150; i++) crumb.Invoke(scope, new object[] {"c", i.ToString(), null, 1L});
        for (var i = 0; i < 100; i++) log.Invoke(scope, new object[] {new string('x', 1000), 1L});

        var payload = new Dictionary<string, object>();
        write.Invoke(scope, new object[] {payload});

        Program.Require(((Dictionary<string, object>) payload["keys"]).Count == 64, "scope: key cap not held");
        var crumbs = (List<object>) payload["breadcrumbs"];
        Program.Require(crumbs.Count == 100, "scope: breadcrumb cap not held");
        Program.Require((string) ((Dictionary<string, object>) crumbs[0])["message"] == "50", "scope: oldest crumbs first");
        Program.Require(((string) payload["log"]).Length <= 64 * 1024, "scope: log cap not held");
        Program.Require(((string) payload["log"]).EndsWith("\n"), "scope: log cut mid-line");
        Console.WriteLine("scope: keys, breadcrumbs and log hold their caps");
    }

    internal static void CheckDebugId()
    {
        var of = SdkType("DebugId").GetMethod("Of", BindingFlags.NonPublic | BindingFlags.Static);
        var id = (string) of.Invoke(null, new object[] {typeof(CrashChecks).Module});

        Program.Require(id != null, "debugId: the gate's own module has a CodeView record");
        Program.Require(Regex.IsMatch(id, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-[0-9a-f]+$"),
            "debugId: shape " + id);
        Console.WriteLine("debugId: " + id + " read from the PE by hand");
    }

    /// <summary>A minidump built by hand: header, directory, an exception
    /// stream for an access violation, and one module with an RSDS record.</summary>
    internal static void CheckMinidump()
    {
        var bytes = new MemoryStream();
        var w = new BinaryWriter(bytes);

        // Layout: header (32) | directory (2 × 12 = 24, at 32) | exception
        // stream (at 56, 168 bytes) | module list (at 224) | strings/cv after.
        const uint directoryAt = 32, exceptionAt = 56, modulesAt = 224;
        const ulong moduleBase = 0x7ff600000000;
        const uint moduleSize = 0x20000;
        var guid = Guid.Parse("5db7294d-87fc-4726-a5c0-4a90679657b5");

        w.Write(0x504D444Du); w.Write(0xA793u); w.Write(2u); w.Write(directoryAt); w.Write(0u); w.Write(0u); w.Write(0uL);
        // directory
        w.Write(6u); w.Write(168u); w.Write(exceptionAt);
        w.Write(4u); w.Write(4u + 108u); w.Write(modulesAt);
        // exception stream
        w.Write(4242u); w.Write(0u);
        w.Write(0xC0000005u); w.Write(0u); w.Write(0uL); w.Write(moduleBase + 0x1234); w.Write(2u); w.Write(0u);
        w.Write(1uL); w.Write(0x10uL);
        for (var i = 2; i < 15; i++) w.Write(0uL);
        w.Write(0u); w.Write(0u); // ThreadContext
        Program.Require(bytes.Position == modulesAt, "minidump fixture: module list offset " + bytes.Position);
        // module list: count, then one MINIDUMP_MODULE
        const uint nameAt = modulesAt + 4 + 108;
        const uint cvAt = nameAt + 4 + 24 + 2;
        w.Write(1u);
        w.Write(moduleBase); w.Write(moduleSize); w.Write(0u); w.Write(0u); w.Write(nameAt);
        w.Write(new byte[52]);
        w.Write(24u + 12u); w.Write(cvAt); w.Write(0u); w.Write(0u); w.Write(0uL); w.Write(0uL);
        // name: MINIDUMP_STRING
        var name = Encoding.Unicode.GetBytes("Checkout.dll");
        w.Write((uint) name.Length); w.Write(name); w.Write((ushort) 0);
        Program.Require(bytes.Position == cvAt, "minidump fixture: cv offset " + bytes.Position);
        w.Write(0x53445352u); w.Write(guid.ToByteArray()); w.Write(1u); w.Write(Encoding.ASCII.GetBytes("Checkout.pdb\0"));
        w.Flush();

        var reader = new BinaryReader(new MemoryStream(bytes.ToArray()));
        var read = SdkType("WerDumps+Minidump").GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] {typeof(BinaryReader)}, null);
        var dump = (Dictionary<string, object>) read.Invoke(null, new object[] {reader});

        Program.Require(dump != null, "minidump: not read");
        Program.Require((string) dump["type"] == "EXCEPTION_ACCESS_VIOLATION", "minidump: type " + dump["type"]);
        Program.Require(((string) dump["message"]).Contains("writing 0x10") && ((string) dump["message"]).Contains("Checkout.dll+0x1234"),
            "minidump: message " + dump["message"]);
        var frame = (Dictionary<string, object>) ((List<object>) dump["frames"])[0];
        Program.Require((string) frame["relativeAddr"] == "0x1234", "minidump: relative address");
        Program.Require((string) frame["buildId"] == "5db7294d-87fc-4726-a5c0-4a90679657b5-1", "minidump: debug id " + frame["buildId"]);
        Program.Require((uint) dump["threadId"] == 4242u, "minidump: thread");
        Console.WriteLine("minidump: the exception record and the faulting module's debug id are read");
    }

    /// <summary>A UI loop the gate owns: the watchdog posts through it, the
    /// gate stops pumping, the hang fires; pumping resumes, it recovers.</summary>
    internal static void CheckWatchdog()
    {
        var pump = new PumpContext {Pumping = true};
        var hangs = 0;
        var recoveries = 0;
        var watchdog = (IDisposable) null;
        var type = SdkType("HangWatchdog");
        var instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] {pump, 200, new Action<long>(ms => Interlocked.Increment(ref hangs)), new Action(() => Interlocked.Increment(ref recoveries))},
            null);
        type.GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, null);

        Thread.Sleep(700);
        Program.Require(hangs == 0, "watchdog: a pumping loop is not a hang");

        pump.Pumping = false;
        Program.SpinUntil(() => hangs == 1);
        Thread.Sleep(500);
        Program.Require(hangs == 1, "watchdog: one report per freeze, got " + hangs);

        pump.Pumping = true;
        Program.SpinUntil(() => recoveries == 1);
        Program.Require(recoveries == 1, "watchdog: the recovery is seen");
        type.GetMethod("Stop", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, null);
        watchdog?.Dispose();
        Console.WriteLine("watchdog: a stalled UI loop is caught once and its recovery seen");
    }

    private sealed class PumpContext : SynchronizationContext
    {
        internal volatile bool Pumping;

        public override void Post(SendOrPostCallback callback, object state)
        {
            // Runs the callback only while "the loop turns".
            if (Pumping) ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    // --- the process ---------------------------------------------------------------------------

    internal static void CheckProcessDeaths(string outDir)
    {
        // An unreachable server: whatever the runs enqueue stays on disk to read.
        const string dead = "http://127.0.0.1:9";

        // 1. A managed crash on the main thread: written at the instant, with
        //    the session's end, and the next start sees crashedLastRun.
        var dataDir = Path.Combine(outDir, "crash-throw");
        var status = Program.RunChild(new[] {"--victim", "throw", dataDir, dead});
        Program.Require(status != 0, "throw: the victim must die, status " + status);

        var items = QueuedItems(dataDir);
        var crash = FirstItem(items, "crash", "uncaughtExceptionHandler");
        Program.Require(crash != null, "throw: no crash was written at the instant");
        Program.Require(((Dictionary<string, object>) crash["user"])["id"] as string == "u-gate", "throw: scope did not ride");
        Program.Require(((Dictionary<string, object>) crash["keys"])["mode"] as string == "throw", "throw: keys lost");
        Program.Require(((List<object>) crash["breadcrumbs"]).Count == 1, "throw: breadcrumbs lost");
        Program.Require(((string) crash["log"]).Contains("the last line"), "throw: log lost");
        var context = (Dictionary<string, object>) crash["context"];
        Program.Require(context.ContainsKey("memoryWorkingSetBytes") && context.ContainsKey("timeToCrashMs"), "throw: facts missing");
        var ended = SessionWithStatus(items, "crashed");
        Program.Require(ended != null && (string) ended["sid"] == (string) crash["sessionId"], "throw: the session's end must travel with the crash");

        status = Program.RunChild(new[] {"--boot", dataDir, dead});
        Program.Require(status == 0, "throw: the boot child failed");
        Program.Require(File.ReadAllText(Path.Combine(dataDir, "boot-result.txt")).Contains("crashedLastRun=True"),
            "throw: the next start must know");
        Console.WriteLine("process: a main-thread crash is written at the instant, with its session; the next start knows");

        // 2. A worker thread: the same, on a named thread.
        dataDir = Path.Combine(outDir, "crash-thread");
        Program.RunChild(new[] {"--victim", "thread", dataDir, dead});
        crash = FirstItem(QueuedItems(dataDir), "crash", "uncaughtExceptionHandler");
        Program.Require(crash != null, "thread: no crash written");
        var thread = (Dictionary<string, object>) ((List<object>) crash["threads"])[0];
        Program.Require((string) thread["name"] == "gate-worker", "thread: crashed thread named " + thread["name"]);
        Console.WriteLine("process: a worker-thread crash names its thread");

        // 3. The async path: frames read as the source, not the state machine.
        dataDir = Path.Combine(outDir, "crash-async");
        Program.RunChild(new[] {"--victim", "async", dataDir, dead});
        crash = FirstItem(QueuedItems(dataDir), "crash", "uncaughtExceptionHandler");
        Program.Require(crash != null, "async: no crash written");
        var frames = ((List<object>) crash["exceptions"]).Cast<Dictionary<string, object>>().First()["frames"] as List<object>;
        Program.Require(frames.Cast<Dictionary<string, object>>().Any(f => (string) f["function"] == "DieAsync" && (string) f["module"] == "CrashChecks"),
            "async: DieAsync must appear demangled");
        Console.WriteLine("process: an async crash reads as the source");

        // 4. A recorded error: an error item, and the session's first error.
        dataDir = Path.Combine(outDir, "crash-record");
        status = Program.RunChild(new[] {"--victim", "record", dataDir, dead});
        Program.Require(status == 0, "record: the victim must live");
        items = QueuedItems(dataDir);
        var error = FirstItem(items, "error", "recordError");
        Program.Require(error != null, "record: no error item");
        Program.Require((bool) ((Dictionary<string, object>) error["mechanism"])["handled"], "record: handled");
        Program.Require(items.Any(i => (string) i["type"] == "session" && ((Dictionary<string, object>) i["payload"]).ContainsKey("errors")
                                       && (long) ((Dictionary<string, object>) i["payload"])["errors"] == 1
                                       && !((Dictionary<string, object>) i["payload"]).ContainsKey("duration")),
            "record: the session's first error must count once");
        // Exited through ProcessExit: the next start finds a clean run, and sends nothing for it.
        Program.RunChild(new[] {"--boot", dataDir, dead});
        Program.Require(SessionWithStatus(QueuedItems(dataDir), "abnormal") == null, "record: a clean exit is not abnormal");
        Console.WriteLine("process: a recorded error is an error item; a clean exit ends nothing");

        // 5. Killed: no crash, no clean exit → the session ends abnormal.
        dataDir = Path.Combine(outDir, "crash-kill");
        Program.RunChild(new[] {"--victim", "kill", dataDir, dead});
        Program.RunChild(new[] {"--boot", dataDir, dead});
        items = QueuedItems(dataDir);
        Program.Require(FirstItem(items, "crash", null) == null, "kill: a kill invents no issue");
        Program.Require(SessionWithStatus(items, "abnormal") != null, "kill: the killed session must end abnormal");
        Console.WriteLine("process: a kill ends its session abnormal without inventing an issue");
    }

    internal static int BootChild(string dataDir, string baseUrl)
    {
        Atlas.Start("sdk_gate", baseUrl, dataDir);
        // The settle thread runs on its own; give it its moment, then the worker.
        Thread.Sleep(1_500);
        Atlas.Core.AwaitIdle(5_000);
        File.WriteAllText(Path.Combine(dataDir, "boot-result.txt"),
            "crashedLastRun=" + AtlasCrash.CrashedLastRun + "\ninstalled=" + string.Join(",", Installed()));

        return 0;
    }

    private static IEnumerable<string> Installed()
    {
        return (IEnumerable<string>) typeof(AtlasCrash).GetProperty("Installed", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    }

    // --- envelopes on disk ----------------------------------------------------------------------

    /// <summary>Every item in every envelope under the data directory's
    /// queue, whichever process's subdirectory it sits in.</summary>
    internal static List<Dictionary<string, object>> QueuedItems(string dataDir)
    {
        var items = new List<Dictionary<string, object>>();
        var queue = Path.Combine(dataDir, "queue");

        if (!Directory.Exists(queue)) return items;

        foreach (var file in Directory.GetFiles(queue, "*.envelope", SearchOption.AllDirectories).OrderBy(Path.GetFileName))
        {
            items.AddRange(ItemsOf(File.ReadAllBytes(file)));
        }

        return items;
    }

    private static List<Dictionary<string, object>> ItemsOf(byte[] bytes)
    {
        var items = new List<Dictionary<string, object>>();
        var at = Array.IndexOf(bytes, (byte) '\n') + 1; // the header

        while (at < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte) '\n', at);
            var head = JsonObject(Encoding.UTF8.GetString(bytes, at, end - at));
            var size = (int) (long) head["length"];
            var payload = JsonObject(Encoding.UTF8.GetString(bytes, end + 1, size));
            items.Add(new Dictionary<string, object> {["type"] = head["type"], ["payload"] = payload});
            at = end + 1 + size + 1;
        }

        return items;
    }

    private static Dictionary<string, object> JsonObject(string text)
    {
        var parse = SdkType("JsonReader").GetMethod("Object", BindingFlags.NonPublic | BindingFlags.Static);

        return (Dictionary<string, object>) parse.Invoke(null, new object[] {text});
    }

    private static Dictionary<string, object> FirstItem(List<Dictionary<string, object>> items, string type, string mechanism)
    {
        foreach (var item in items)
        {
            if ((string) item["type"] != type) continue;

            var payload = (Dictionary<string, object>) item["payload"];

            if (mechanism == null || (string) ((Dictionary<string, object>) payload["mechanism"])["type"] == mechanism) return payload;
        }

        return null;
    }

    private static Dictionary<string, object> SessionWithStatus(List<Dictionary<string, object>> items, string status)
    {
        foreach (var item in items)
        {
            var payload = (Dictionary<string, object>) item["payload"];

            if ((string) item["type"] == "session" && payload["status"] as string == status) return payload;
        }

        return null;
    }

    // --- the samples every SDK's golden pins ---------------------------------------------------

    internal static void WriteSamples(string outDir)
    {
        var context = new Dictionary<string, object>
        {
            ["device"] = new Dictionary<string, object> {["os"] = "windows", ["osVersion"] = "10.0.22631", ["arch"] = "x64"},
            ["app"] = new Dictionary<string, object> {["version"] = "3.2.1", ["build"] = "151"},
        };

        Exception caught = null;

        try
        {
            ThrowDeep(1);
        }
        catch (Exception thrown)
        {
            caught = thrown;
        }

        // Real frames vary with the build; the golden pins the shape with
        // frames written by hand.
        var frames = new List<object>
        {
            new Dictionary<string, object>
            {
                ["module"] = "Shop.Checkout.CartPage", ["function"] = "OnBuy", ["file"] = "CartPage.xaml.cs", ["line"] = 42,
                ["methodToken"] = "0x06000012", ["ilOffset"] = 17,
                ["debugId"] = "5db7294d-87fc-4726-a5c0-4a90679657b5-1", ["assembly"] = "Shop",
            },
            new Dictionary<string, object>
            {
                ["module"] = "System.Windows.Threading.ExceptionWrapper", ["function"] = "InternalRealCall",
                ["methodToken"] = "0x06000af3", ["ilOffset"] = 80, ["assembly"] = "WindowsBase",
            },
        };
        var crash = Payload(caught, CrashReport("MechanismUncaught"), false, false);
        ((Dictionary<string, object>) ((List<object>) crash["exceptions"])[0])["frames"] = frames;
        ((Dictionary<string, object>) ((List<object>) crash["exceptions"])[1])["frames"] = new List<object> {frames[0]};
        crash["eventId"] = "44444444-0000-0000-0000-000000000001";
        crash["crashedAt"] = "2026-09-18T01:00:00Z";
        crash["sessionId"] = "55555555-0000-0000-0000-000000000001";
        crash["threads"] = new List<object> {new Dictionary<string, object> {["name"] = "main", ["crashed"] = true, ["frames"] = frames}};
        crash["user"] = new Dictionary<string, object> {["id"] = "u-123"};
        crash["keys"] = new Dictionary<string, object> {["screen"] = "checkout"};
        crash["breadcrumbs"] = new List<object>
        {
            new Dictionary<string, object> {["ts"] = "2026-09-18T09:00:00Z", ["category"] = "cart", ["level"] = "info", ["message"] = "add \"socks\""},
        };
        crash["context"] = new Dictionary<string, object> {["startedAt"] = "2026-09-18T00:58:00Z", ["timeToCrashMs"] = 120000L, ["memoryWorkingSetBytes"] = 187654144L};

        var session = SdkType("SessionItems").GetMethod("Ended", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] {"44444444-0000-0000-0000-000000000002", "55555555-0000-0000-0000-000000000001", "crashed", "2026-09-18T00:58:00Z", 0, 120000L});

        File.WriteAllBytes(Path.Combine(outDir, "crash.envelope"),
            new Program.EnvelopeWriterProxy("atlas-dotnet", context, "2026-09-18T01:00:05Z")
                .Add("crash", crash)
                .Add("session", (Dictionary<string, object>) session)
                .Bytes());

        var error = Payload(caught.InnerException, CrashReport("MechanismRecorded"), true, false);
        ((Dictionary<string, object>) ((List<object>) error["exceptions"])[0])["frames"] = new List<object> {frames[0]};
        error["eventId"] = "44444444-0000-0000-0000-000000000003";
        error["crashedAt"] = "2026-09-18T01:00:00Z";
        error["sessionId"] = "55555555-0000-0000-0000-000000000001";
        var errored = SdkType("SessionItems").GetMethod("Errored", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] {"44444444-0000-0000-0000-000000000004", "55555555-0000-0000-0000-000000000001", "2026-09-18T00:58:00Z"});

        File.WriteAllBytes(Path.Combine(outDir, "error.envelope"),
            new Program.EnvelopeWriterProxy("atlas-dotnet", context, "2026-09-18T01:00:05Z")
                .Add("error", error)
                .Add("session", (Dictionary<string, object>) errored)
                .Bytes());
    }

    // --- plumbing ---------------------------------------------------------------------------------

    private static Type SdkType(string name)
    {
        return typeof(AtlasCrash).Assembly.GetType("AppAtlas.Sdk." + (name.StartsWith("Json") ? name : "Crash." + name));
    }

    private static object Internal(string name)
    {
        return Activator.CreateInstance(SdkType(name), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[0], null);
    }

    private static string CrashReport(string constant)
    {
        return (string) SdkType("CrashReport").GetField(constant, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    }

    private static Dictionary<string, object> Payload(Exception error, string mechanism, bool handled, bool withThread)
    {
        return (Dictionary<string, object>) SdkType("CrashReport").GetMethod("Payload", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] {"e", "2026-09-18T01:00:00Z", "s", mechanism, handled, error, withThread});
    }

    private static void Demangle(ref string type, ref string method)
    {
        var args = new object[] {type, method};
        SdkType("CrashReport").GetMethod("Demangle", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
        type = (string) args[0];
        method = (string) args[1];
    }
}
