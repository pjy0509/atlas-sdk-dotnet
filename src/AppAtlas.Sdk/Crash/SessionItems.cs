using System.Collections.Generic;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// The `session` item in its three moments. The server keeps day totals,
    /// not sessions, so each send says only what it adds: `init` counts a
    /// start, an end state counts that end, and the first handled error
    /// counts once.
    /// </summary>
    internal static class SessionItems
    {
        internal static Dictionary<string, object> Started(string eventId, string sid, string startedIso)
        {
            var payload = Base(eventId, sid, "ok", startedIso);
            payload["init"] = true;

            return payload;
        }

        internal static Dictionary<string, object> Errored(string eventId, string sid, string startedIso)
        {
            var payload = Base(eventId, sid, "ok", startedIso);
            payload["errors"] = 1;

            return payload;
        }

        /// <summary>`status` is `crashed` or `abnormal`; a clean exit is never
        /// sent. A negative duration is left out.</summary>
        internal static Dictionary<string, object> Ended(string eventId, string sid, string status, string startedIso,
            int errors, long durationMs)
        {
            var payload = Base(eventId, sid, status, startedIso);
            payload["errors"] = errors;

            if (durationMs >= 0) payload["duration"] = durationMs / 1000L;

            return payload;
        }

        private static Dictionary<string, object> Base(string eventId, string sid, string status, string startedIso)
        {
            var payload = new Dictionary<string, object>
            {
                ["eventId"] = eventId,
                ["sid"] = sid,
                ["status"] = status,
            };

            if (startedIso != null) payload["started"] = startedIso;

            return payload;
        }
    }
}
