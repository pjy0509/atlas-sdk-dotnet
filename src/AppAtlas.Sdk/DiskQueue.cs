using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AppAtlas.Sdk
{
    /// <summary>
    /// The write-before-send store, one directory per process id: desktop
    /// apps run several instances of one exe as a matter of course, and two
    /// workers over one directory is the cross-process hazard sentry punted
    /// on (docs/sdk-cautions.md §3). A dead instance's leftovers are adopted
    /// at start, so a crash-era envelope still leaves.
    /// </summary>
    internal sealed class DiskQueue
    {
        internal const int MaxFiles = 30;
        private const string Suffix = ".envelope";

        private readonly string _root;
        private readonly string _directory;
        private readonly object _lock = new object();
        private long _counter;

        internal DiskQueue(string root)
        {
            // No I/O here: directories are made on first use, off the caller.
            _root = root;
            _directory = Path.Combine(root, Process.GetCurrentProcess().Id.ToString());
        }

        /// <summary>Moves files stranded by dead instances into this queue.
        /// Called once from the worker at start, before the first drain.</summary>
        internal void AdoptOrphans()
        {
            lock (_lock)
            {
                if (!Directory.Exists(_root)) return;

                Directory.CreateDirectory(_directory);

                foreach (var stray in Directory.GetDirectories(_root))
                {
                    if (stray == _directory || !int.TryParse(Path.GetFileName(stray), out var pid)) continue;
                    if (IsAlive(pid)) continue;

                    foreach (var file in Directory.GetFiles(stray, "*" + Suffix))
                    {
                        try
                        {
                            File.Move(file, Path.Combine(_directory, Path.GetFileName(file)));
                        }
                        catch (IOException)
                        {
                            // A racing sibling adopted it first; theirs now.
                        }
                    }

                    try
                    {
                        Directory.Delete(stray);
                    }
                    catch (IOException)
                    {
                        // Not empty after the race: the next start retries.
                    }
                }
            }
        }

        internal string Offer(byte[] envelope)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_directory);

                var present = List();

                for (var index = 0; index <= present.Count - MaxFiles; index++)
                {
                    TryDelete(present[index]);
                }

                var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var name = stamp.ToString("D13") + "_" + (_counter++ % 1000).ToString("D3") + Suffix;
                var path = Path.Combine(_directory, name);

                try
                {
                    File.WriteAllBytes(path, envelope);
                }
                catch (IOException)
                {
                    // Full disk or a vanished directory: the event is gone,
                    // the app must not be.
                    return null;
                }

                return path;
            }
        }

        /// <summary>Oldest first, the order they should leave in.</summary>
        internal List<string> List()
        {
            if (!Directory.Exists(_directory)) return new List<string>();

            return Directory.GetFiles(_directory, "*" + Suffix).OrderBy(Path.GetFileName).ToList();
        }

        internal static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Another instance won the race; either way it is gone for us.
            }
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                Process.GetProcessById(pid);

                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
