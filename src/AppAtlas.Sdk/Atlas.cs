using System;
using System.IO;

namespace AppAtlas.Sdk
{
    /// <summary>
    /// The entry point: one line at application start.
    ///
    ///     Atlas.Start("sdk_…");
    ///
    /// Modules (links, crash) attach to the core this creates; none of them
    /// touch the network or the disk on their own.
    /// </summary>
    public static class Atlas
    {
        private const string DefaultBaseUrl = "https://appatlas.dev";

        private static readonly object Lock = new object();
        private static AtlasCore _core;
        private static string _dataDir;

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
            Links.AtlasLinks.Boot();
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
