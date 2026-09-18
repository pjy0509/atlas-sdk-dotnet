using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// What a run knows about itself, kept on disk per process id so the next
    /// start can tell how each dead instance ended: a managed crash it wrote
    /// itself, a native death WER left a dump for, a clean exit, or nothing
    /// at all — a kill, a stack overflow no dump caught, a power cut. Desktop
    /// apps run several instances of one exe as a matter of course, so one
    /// file per pid, adopted when the pid is gone (the queue does the same).
    /// </summary>
    internal sealed class RunState
    {
        private readonly string _directory;
        private readonly string _path;
        private readonly object _lock = new object();
        private readonly Dictionary<string, object> _state;

        internal string SessionId { get; }
        internal long StartedAtMs { get; }

        internal RunState(string directory, string sessionId, long startedAtMs)
        {
            SessionId = sessionId;
            StartedAtMs = startedAtMs;
            _directory = directory;
            _path = Path.Combine(directory, Process.GetCurrentProcess().Id + ".json");
            _state = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["startedAt"] = startedAtMs,
                ["cleanExit"] = false,
                ["crashWritten"] = false,
                ["hanging"] = false,
            };
        }

        /// <summary>The runs of instances no longer alive, oldest first; each
        /// file is removed as it is returned, so a death is reported once.</summary>
        internal List<Dictionary<string, object>> TakeDeadRuns()
        {
            var dead = new List<Dictionary<string, object>>();

            if (!Directory.Exists(_directory)) return dead;

            var files = Directory.GetFiles(_directory, "*.json");
            Array.Sort(files, StringComparer.Ordinal);

            foreach (var file in files)
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid) || IsAlive(pid)) continue;

                try
                {
                    var parsed = JsonReader.Object(File.ReadAllText(file));
                    parsed["endedAt"] = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
                    dead.Add(parsed);
                }
                catch (Exception)
                {
                    // Unreadable: the file goes, the death stays unknown.
                }

                DiskQueue.TryDelete(file);
            }

            return dead;
        }

        internal void Set(string key, object value)
        {
            lock (_lock) _state[key] = value;
        }

        /// <summary>Writes the state now, atomically. Off the caller's thread
        /// where it can be; on it when the process is ending.</summary>
        internal void Persist()
        {
            string json;

            lock (_lock) json = Json.Write(_state);

            try
            {
                Directory.CreateDirectory(_directory);
                var fresh = _path + ".tmp";
                File.WriteAllText(fresh, json);

                if (File.Exists(_path)) File.Delete(_path);

                File.Move(fresh, _path);
            }
            catch (Exception)
            {
                // The next change writes again.
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
