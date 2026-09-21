using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

using AppAtlas.Sdk;
using AppAtlas.Sdk.Links;

/// <summary>
/// The .NET half of the envelope parity gate (sdk/windows/check-core.sh runs
/// it): writes envelopes for the server's parser to verify, and exercises
/// the queue, transport verdicts, orphan adoption and the links flow against
/// a throwaway in-process listener — never the real server.
/// </summary>
internal static class Program
{
    private const string Token = "ab12cd34ef56ab78.c2ln-bmF0dXJlLXNsb3Q";

    private static readonly Queue<int> EnvelopeStatuses = new Queue<int>(new[] {200, 400, 429, 500});
    private static string _claimCapture;

    private static int Main(string[] args)
    {
        // The crash gate spawns this very binary as a victim, and as the
        // start that comes after it.
        if (args.Length >= 4 && args[0] == "--victim") return CrashChecks.Victim(args[1], args[2], args[3]);
        if (args.Length >= 3 && args[0] == "--boot") return CrashChecks.BootChild(args[1], args[2]);

        var outDir = args[0];
        Directory.CreateDirectory(outDir);

        using var listener = StartMock(out var baseUrl);

        WriteSamples(outDir);
        CrashChecks.WriteSamples(outDir);
        CheckQueueAndAdoption(Path.Combine(outDir, "queue"));
        CheckTransport(baseUrl);
        CheckLinkUrl();
        CheckJsonRoundTrip();
        CrashChecks.CheckReports();
        CrashChecks.CheckScope();
        CrashChecks.CheckDebugId();
        CrashChecks.CheckMinidump();
        CrashChecks.CheckWatchdog();
        CrashChecks.CheckProcessDeaths(outDir);
        CheckLinksFlow(baseUrl, outDir);

        File.WriteAllText(Path.Combine(outDir, "claim-request.json"), _claimCapture);
        Console.WriteLine("parity: windows envelopes, queue, adoption, transport, crash and links hold");

        return 0;
    }

    /// <summary>This binary again, with arguments; the exit status.</summary>
    internal static int RunChild(string[] arguments)
    {
        var self = Process.GetCurrentProcess().MainModule.FileName;
        var start = new ProcessStartInfo(self) {UseShellExecute = false, RedirectStandardError = true};
        var isDotnetHost = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        if (isDotnetHost) start.ArgumentList.Add(typeof(Program).Assembly.Location);

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var child = Process.Start(start);
        child.StandardError.ReadToEnd();
        child.WaitForExit();

        return child.ExitCode;
    }

    private static void WriteSamples(string outDir)
    {
        var context = new Dictionary<string, object>
        {
            ["device"] = new Dictionary<string, object> {["os"] = "windows", ["osVersion"] = "10.0.22631"},
        };

        File.WriteAllBytes(Path.Combine(outDir, "open.envelope"),
            new EnvelopeWriterProxy("atlas-dotnet", context)
                .Add("open", new Dictionary<string, object>
                {
                    ["eventId"] = "11111111-2222-3333-4444-555555555555",
                    ["shortId"] = "aB3kM9p",
                    ["channel"] = "email",
                }).Bytes());

        File.WriteAllBytes(Path.Combine(outDir, "hostile.envelope"),
            new EnvelopeWriterProxy("atlas-dotnet", null)
                .Add("open", new Dictionary<string, object>
                {
                    ["eventId"] = "22222222-0000-0000-0000-000000000002",
                    ["note"] = "line\nbreak \"quoted\" back\\slash 한글 ctl:\u0001",
                    ["nested"] = new Dictionary<string, object> {["deep"] = true, ["count"] = 42},
                }).Bytes());

        File.WriteAllBytes(Path.Combine(outDir, "pair.envelope"),
            new EnvelopeWriterProxy("atlas-dotnet", null)
                .Add("open", new Dictionary<string, object> {["eventId"] = "33333333-0000-0000-0000-000000000001"})
                .Add("session", new Dictionary<string, object> {["eventId"] = "33333333-0000-0000-0000-000000000002"})
                .Bytes());
    }

    private static void CheckQueueAndAdoption(string root)
    {
        var queue = Internal("DiskQueue", root);
        var offer = queue.GetType().GetMethod("Offer", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = queue.GetType().GetMethod("List", BindingFlags.NonPublic | BindingFlags.Instance);

        for (var index = 0; index < 35; index++)
        {
            Require(offer.Invoke(queue, new object[] {new byte[] {1, 2, 3}}) != null, "offer failed");
        }

        var listed = (List<string>) list.Invoke(queue, null);
        Require(listed.Count == 30, "cap not held: " + listed.Count);
        Require(listed.SequenceEqual(listed.OrderBy(Path.GetFileName)), "order broken");

        // A dead instance's directory: its files must be adopted at start.
        var orphan = Path.Combine(root, "999999");
        Directory.CreateDirectory(orphan);
        File.WriteAllBytes(Path.Combine(orphan, "0000000000000_000.envelope"), new byte[] {9});

        var adopt = queue.GetType().GetMethod("AdoptOrphans", BindingFlags.NonPublic | BindingFlags.Instance);
        adopt.Invoke(queue, null);

        Require(!Directory.Exists(orphan), "the orphan directory must be gone");
        listed = (List<string>) list.Invoke(queue, null);
        Require(listed.Any(path => Path.GetFileName(path) == "0000000000000_000.envelope"),
            "the orphan's envelope must be adopted");
    }

    private static void CheckTransport(string baseUrl)
    {
        var transport = Internal("Transport", baseUrl, "sdk_test");
        var send = transport.GetType().GetMethod("Send", BindingFlags.NonPublic | BindingFlags.Instance);
        var limited = transport.GetType().GetMethod("Limited", BindingFlags.NonPublic | BindingFlags.Instance);
        var body = new byte[] {1};

        string Verdict(long now) => send.Invoke(transport, new object[] {body, now}).ToString();

        Require(Verdict(0) == "Delivered", "2xx must deliver");
        Require(Verdict(0) == "Refused", "4xx must refuse");
        Require(Verdict(0) == "RetryLater", "429 must defer");
        Require((bool) limited.Invoke(transport, new object[] {29_000L}), "the Retry-After deadline must hold");
        Require(!(bool) limited.Invoke(transport, new object[] {31_000L}), "the deadline must expire");
        Require(Verdict(40_000) == "RetryLater", "5xx must retry");

        // A big envelope leaves gzipped and reads back to the same bytes; a small one as it is.
        var gzip = transport.GetType().GetMethod("Gzip", BindingFlags.NonPublic | BindingFlags.Static);
        var big = new byte[8192];
        Array.Fill(big, (byte) 'a');
        var packed = (byte[]) gzip.Invoke(null, new object[] {big});
        Require(packed.Length < 512, "gzip must shrink a repetitive envelope");

        using (var unpack = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress))
        using (var back = new System.IO.MemoryStream())
        {
            unpack.CopyTo(back);
            Require(back.ToArray().AsSpan().SequenceEqual(big), "gzip must round-trip");
        }

        Require(ReferenceEquals(gzip.Invoke(null, new object[] {body}), body), "a small envelope goes as it is");

        // The key the project file stamped into this assembly is what a code-free start reads.
        var configured = typeof(Atlas).GetMethod("Configured", BindingFlags.NonPublic | BindingFlags.Static);
        Require((string) configured.Invoke(null, new object[] {"AppAtlas.SdkKey", "ATLAS_SDK_KEY"}) == "sdk_stamped",
            "the stamped key must be read from the entry assembly");
        Require((string) configured.Invoke(null, new object[] {"AppAtlas.BaseUrl", "ATLAS_BASE_URL"}) == "http://127.0.0.1:9",
            "the stamped base URL must be read too");
        Environment.SetEnvironmentVariable("ATLAS_PARITY_PROBE", "from-env");
        Require((string) configured.Invoke(null, new object[] {"AppAtlas.Nothing", "ATLAS_PARITY_PROBE"}) == "from-env",
            "the environment stands in when the assembly says nothing");
        Require(typeof(Atlas).Assembly.GetType("StartupHook")?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static) != null,
            "the runtime's startup hook must exist in the shape it looks for");
    }

    private static void CheckLinkUrl()
    {
        var parse = typeof(AtlasLink).Assembly.GetType("AppAtlas.Sdk.Links.LinkUrl")
            .GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);

        object Parse(string url) => parse.Invoke(null, new object[] {url});
        string Field(object link, string name) => (string) link.GetType()
            .GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(link);

        var visit = Parse("https://appatlas.dev/aB3kM9p?ch=email&cp=spring%202026&src=qr");
        Require(visit != null && Field(visit, "ShortId") == "aB3kM9p", "visit URL must parse");
        Require(Field(visit, "Campaign") == "spring 2026", "cp not decoded");

        var handoff = Parse("https://appatlas.dev/c/" + Token);
        Require(handoff != null && Field(handoff, "ClaimToken") == Token, "handoff token lost");

        Require(Parse("https://appatlas.dev/install") == null, "an excluded letter must refuse");
        Require(Parse("not a url") == null, "garbage must be null");
    }

    private static void CheckJsonRoundTrip()
    {
        var assembly = typeof(AtlasLink).Assembly;
        var write = assembly.GetType("AppAtlas.Sdk.Json").GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Static);
        var parse = assembly.GetType("AppAtlas.Sdk.JsonReader").GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);

        var value = new Dictionary<string, object>
        {
            ["note"] = "line\nbreak \"quoted\" back\\slash 한글 ctl:\u0001",
            ["count"] = 42L,
            ["nothing"] = null,
        };
        var back = (Dictionary<string, object>) parse.Invoke(null, new object[] {write.Invoke(null, new object[] {value})});

        Require((string) back["note"] == (string) value["note"], "json round trip drifted");
        Require((long) back["count"] == 42L, "numbers drifted");
        Require(parse.Invoke(null, new object[] {"not json"}) == null, "garbage must read as null");
    }

    private static void CheckLinksFlow(string baseUrl, string outDir)
    {
        var dataDir = Path.Combine(outDir, "state");
        // A fixed install id keeps the claim body byte-stable for the golden;
        // Start still reads it through its own path.
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "install-id"), "install-1");
        Atlas.Start("sdk_test", baseUrl, dataDir);
        Require(Atlas.Core != null, "the core must start");

        // The activation landed before the listener registered: queue, replay.
        AtlasLink received = null;
        Require(AtlasLinks.Handle("https://appatlas.dev/aB3kM9p?ch=email"), "a visit URL must be handled");
        AtlasLinks.SetListener(link => received = link);
        Require(received != null && !received.Deferred, "the queued direct link must replay");
        Require(received.ShortId == "aB3kM9p" && received.Channel == "email", "link facts lost");

        Require(!AtlasLinks.Handle("https://appatlas.dev/settings"), "a stranger URL must be refused");

        // The deferred claim: the mock answers 200; the ack sets the flag,
        // the payload arrives, and a second claim is refused by the flag.
        received = null;
        AtlasLinks.ClaimCampaignId(Token);
        SpinUntil(() => received != null);
        Require(received != null && received.Deferred, "the deferred link never arrived");
        Require((string) received.Payload["promo"] == "launch", "payload lost");
        Require(AtlasLinks.FirstReferringLink() != null, "firstReferringLink must persist");

        received = null;
        AtlasLinks.ClaimCampaignId("ffffffffffffffff.ZmFrZS1zZWNvbmQtdG9rZW4");
        Thread.Sleep(300);
        Require(received == null, "claim-done must end the attempts");

        Atlas.Core.AwaitIdle(5_000);
    }

    // --- plumbing -------------------------------------------------------------

    /// <summary>The gate reaches internals the way the module does; the
    /// public surface stays exactly the public surface.</summary>
    private static object Internal(string name, params object[] args)
    {
        var type = typeof(AtlasLink).Assembly.GetType("AppAtlas.Sdk." + name);

        return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, args, null);
    }

    internal sealed class EnvelopeWriterProxy
    {
        private readonly object _writer;

        internal EnvelopeWriterProxy(string sdkName, Dictionary<string, object> context, string sentAt = "2026-09-15T09:00:00Z")
        {
            var type = typeof(AtlasLink).Assembly.GetType("AppAtlas.Sdk.EnvelopeWriter");
            _writer = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] {sdkName, "0.1.0", sentAt, "c1a2b3d4e5f60718", context}, null);
        }

        internal EnvelopeWriterProxy Add(string type, Dictionary<string, object> payload)
        {
            _writer.GetType().GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_writer, new object[] {type, payload});

            return this;
        }

        internal byte[] Bytes()
        {
            return (byte[]) _writer.GetType().GetMethod("Bytes", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_writer, null);
        }
    }

    private static HttpListener StartMock(out string baseUrl)
    {
        var port = new Random().Next(20000, 40000);
        var listener = new HttpListener();
        baseUrl = "http://127.0.0.1:" + port;
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();

        var answer = "{\"payload\":{\"promo\":\"launch\"},\"path\":\"spotify://\","
                     + "\"clickedAt\":\"2026-09-15T09:00:00+00:00\",\"channel\":\"email\","
                     + "\"campaign\":null,\"match\":\"campaign_id\"}";

        var pump = new Thread(() =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;

                try
                {
                    context = listener.GetContext();
                }
                catch (Exception)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = reader.ReadToEnd();
                byte[] response;

                if (context.Request.Url.AbsolutePath.EndsWith("/link/claim"))
                {
                    _claimCapture ??= body;
                    context.Response.StatusCode = 200;
                    response = Encoding.UTF8.GetBytes(answer);
                }
                else
                {
                    var status = EnvelopeStatuses.Count > 1 ? EnvelopeStatuses.Dequeue() : EnvelopeStatuses.Peek();
                    context.Response.StatusCode = status;

                    if (status == 429) context.Response.AddHeader("Retry-After", "30");

                    response = Encoding.UTF8.GetBytes("{}");
                }

                context.Response.ContentLength64 = response.Length;
                context.Response.OutputStream.Write(response, 0, response.Length);
                context.Response.Close();
            }
        }) {IsBackground = true};
        pump.Start();

        return listener;
    }

    internal static void SpinUntil(Func<bool> check)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!check() && DateTime.UtcNow < deadline) Thread.Sleep(50);
    }

    internal static void Require(bool held, string complaint)
    {
        if (held) return;

        Console.Error.WriteLine("FAIL: " + complaint);
        Environment.Exit(1);
    }
}
