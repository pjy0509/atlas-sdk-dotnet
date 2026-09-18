using System.Collections.Generic;
using System.Text;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// What the app told us about its own state: a user id, custom keys, a
    /// ring of breadcrumbs and a rolling log. Bounded everywhere, because it
    /// rides every report and is copied on a thread that is about to die.
    /// </summary>
    internal sealed class CrashScope
    {
        internal const int MaxKeys = 64;
        internal const int MaxValueChars = 1024;
        internal const int MaxBreadcrumbs = 100;
        internal const int MaxLogChars = 64 * 1024;

        private readonly object _lock = new object();
        private string _userId;
        private readonly Dictionary<string, string> _keys = new Dictionary<string, string>();
        private readonly List<string> _keyOrder = new List<string>();
        private readonly Dictionary<string, object>[] _ring = new Dictionary<string, object>[MaxBreadcrumbs];
        private int _written;
        private readonly StringBuilder _log = new StringBuilder();

        internal void SetUserId(string id)
        {
            lock (_lock) _userId = string.IsNullOrEmpty(id) ? null : Clip(id, 128);
        }

        internal void SetKey(string name, string value)
        {
            if (name == null) return;

            var key = Clip(name, 64);

            lock (_lock)
            {
                if (value == null)
                {
                    if (_keys.Remove(key)) _keyOrder.Remove(key);

                    return;
                }

                if (_keys.ContainsKey(key))
                {
                    _keys[key] = Clip(value, MaxValueChars);
                }
                else if (_keys.Count < MaxKeys)
                {
                    _keys[key] = Clip(value, MaxValueChars);
                    _keyOrder.Add(key);
                }
            }
        }

        internal void LeaveBreadcrumb(string category, string message, string level, long atMs)
        {
            var crumb = new Dictionary<string, object>
            {
                ["ts"] = AtlasCore.Iso(atMs),
                ["category"] = Clip(category ?? "", 40),
                ["level"] = level == null ? "info" : Clip(level, 10),
                ["message"] = Clip(message ?? "", 500),
            };

            lock (_lock) _ring[_written++ % MaxBreadcrumbs] = crumb;
        }

        /// <summary>A rolling log: the newest 64 KB of lines, the oldest dropped whole.</summary>
        internal void Log(string line, long atMs)
        {
            if (line == null) return;

            lock (_lock)
            {
                _log.Append(AtlasCore.Iso(atMs)).Append(' ').Append(Clip(line, 4096)).Append('\n');

                if (_log.Length > MaxLogChars)
                {
                    var text = _log.ToString();
                    var cut = text.IndexOf('\n', text.Length - MaxLogChars);
                    _log.Remove(0, cut < 0 ? text.Length - MaxLogChars : cut + 1);
                }
            }
        }

        /// <summary>Copies the scope into a report payload; absent parts are left out.</summary>
        internal void WriteTo(Dictionary<string, object> payload)
        {
            lock (_lock)
            {
                if (_userId != null) payload["user"] = new Dictionary<string, object> {["id"] = _userId};

                if (_keys.Count > 0)
                {
                    var keys = new Dictionary<string, object>();

                    foreach (var name in _keyOrder) keys[name] = _keys[name];

                    payload["keys"] = keys;
                }

                if (_written > 0)
                {
                    var crumbs = new List<object>();
                    var count = _written < MaxBreadcrumbs ? _written : MaxBreadcrumbs;

                    // Oldest first, as they happened.
                    for (var i = _written - count; i < _written; i++) crumbs.Add(_ring[i % MaxBreadcrumbs]);

                    payload["breadcrumbs"] = crumbs;
                }

                if (_log.Length > 0) payload["log"] = _log.ToString();
            }
        }

        private static string Clip(string text, int limit)
        {
            return text.Length > limit ? text.Substring(0, limit) : text;
        }
    }
}
