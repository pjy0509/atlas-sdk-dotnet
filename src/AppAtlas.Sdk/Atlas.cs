using System;
using System.IO;
using System.Reflection;

namespace AppAtlas.Sdk
{
    /// <summary>
    /// The entry point: one line at application start.
    ///
    ///     Atlas.Start("sdk_…");
    ///
    /// Or none: with `&lt;AtlasSdkKey&gt;` in the project file the package
    /// stamps the key into the entry assembly, `Atlas.Start()` reads it
    /// there, and on .NET Core the runtime's startup hook calls Start before
    /// Main. Modules (crash, links) attach to the core this creates; none of
    /// them touch the network or the disk on their own.
    /// </summary>
    public static class Atlas
    {
        private const string DefaultBaseUrl = "https://appatlas.dev";
        // The assembly metadata the package's targets write from the project
        // file, and the environment variables a launcher may set instead.
        internal const string KeyMetadata = "AppAtlas.SdkKey";
        internal const string BaseUrlMetadata = "AppAtlas.BaseUrl";
        internal const string KeyVariable = "ATLAS_SDK_KEY";
        internal const string BaseUrlVariable = "ATLAS_BASE_URL";

        private static readonly object Lock = new object();
        private static AtlasCore _core;
        private static string _dataDir;

        /// <summary>The start with no key in code: the key comes from the entry
        /// assembly's metadata (the project file's `AtlasSdkKey`) or the
        /// `ATLAS_SDK_KEY` environment variable. Quiet when neither names one.</summary>
        public static void Start()
        {
            var key = Configured(KeyMetadata, KeyVariable);

            if (string.IsNullOrEmpty(key)) return;

            Start(key, Configured(BaseUrlMetadata, BaseUrlVariable) ?? DefaultBaseUrl, null);
        }

        public static void Start(string sdkKey)
        {
            Start(sdkKey, DefaultBaseUrl, null);
        }

        public static void Start(string sdkKey, string baseUrl)
        {
            Start(sdkKey, baseUrl, null);
        }

        /// <summary>The base URL override exists for self-hosted and staging
        /// servers; the data directory one for tests and unusual hosts.</summary>
        public static void Start(string sdkKey, string baseUrl, string dataDir)
        {
            lock (Lock)
            {
                if (_core != null) return;

                _dataDir = dataDir ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppAtlas");

                _core = new AtlasCore(
                    "atlas-dotnet",
                    baseUrl ?? DefaultBaseUrl,
                    sdkKey,
                    Path.Combine(_dataDir, "queue"),
                    InstallId(),
                    DeviceContext.Snapshot());
            }

            // A single assembly holds every module, so the boot is a plain
            // call — the reflective dance is for platforms that split them.
            // Crash first: its hooks should be in place before anything else runs.
            Crash.AtlasCrash.Boot();
            Links.AtlasLinks.Boot();
        }

        /// <summary>A value the build stamped into the entry assembly, else the
        /// environment's. Read through the attribute data, never by
        /// instantiating attributes, so a trimmed app still answers.</summary>
        internal static string Configured(string metadataKey, string variable)
        {
            try
            {
                var entry = Assembly.GetEntryAssembly();

                if (entry != null)
                {
                    foreach (var attribute in entry.GetCustomAttributesData())
                    {
                        if (attribute.AttributeType != typeof(AssemblyMetadataAttribute)
                            || attribute.ConstructorArguments.Count != 2) continue;

                        if (attribute.ConstructorArguments[0].Value as string == metadataKey)
                        {
                            var value = attribute.ConstructorArguments[1].Value as string;

                            if (!string.IsNullOrEmpty(value)) return value;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A host with no entry assembly, or metadata it will not show.
            }

            try
            {
                var fromEnvironment = Environment.GetEnvironmentVariable(variable);

                return string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The running core, for modules; null before Start.</summary>
        public static AtlasCore Core
        {
            get { lock (Lock) return _core; }
        }

        internal static string DataDir
        {
            get { lock (Lock) return _dataDir; }
        }

        /// <summary>An install-scoped random id, minted on first start and
        /// kept in a file beside the queue. Never a device identifier.</summary>
        private static string InstallId()
        {
            var path = Path.Combine(_dataDir, "install-id");

            try
            {
                if (File.Exists(path)) return File.ReadAllText(path).Trim();
            }
            catch (IOException)
            {
                // Unreadable: mint a fresh one below.
            }

            var minted = Guid.NewGuid().ToString("N").Substring(0, 16);

            try
            {
                Directory.CreateDirectory(_dataDir);
                File.WriteAllText(path, minted);
            }
            catch (IOException)
            {
                // A disk that refuses gets a per-run id; events still flow.
            }

            return minted;
        }
    }
}
