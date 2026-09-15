using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AppAtlas.Sdk.Links
{
    /// <summary>
    /// The links module: direct opens handed in from URI protocol activation,
    /// and the Microsoft Store campaign id claimed once after install. One
    /// listener for both. Attach after Atlas.Start:
    ///
    ///     AtlasLinks.SetListener(link => Route(link));
    ///     AtlasLinks.Handle(activationUri);
    ///     AtlasLinks.ClaimCampaignId(cid);   // from StoreContext, once known
    ///
    /// The campaign id is handed in rather than fetched: reading it needs
    /// WinRT (packaged apps only), and a netstandard2.0 assembly reaching
    /// for WinRT by reflection breaks differently on every host — the app
    /// knows its own packaging, the SDK cannot (docs/sdk-cautions.md §3).
    /// </summary>
    public static class AtlasLinks
    {
        private static readonly object Lock = new object();
        // Links that arrived before the listener did — activation lands
        // before application code can register.
        private static readonly List<AtlasLink> Queue = new List<AtlasLink>();
        private static Action<AtlasLink> _listener;

        /// <summary>Called by Atlas.Start; not application API.</summary>
        internal static void Boot()
        {
        }

        /// <summary>The one listener, direct and deferred alike. Setting it
        /// replaces the previous one and replays whatever arrived before it
        /// was attached; null detaches.</summary>
        public static void SetListener(Action<AtlasLink> listener)
        {
            List<AtlasLink> replay;

            lock (Lock)
            {
                _listener = listener;
                replay = new List<AtlasLink>(Queue);
                Queue.Clear();
            }

            foreach (var link in replay) Deliver(link);
        }

        /// <summary>The link that survived the install, set once ever; null before then.</summary>
        public static AtlasLink FirstReferringLink()
        {
            var stored = ReadState("first-link.json");

            if (stored == null) return null;

            var parsed = JsonReader.Object(stored);
            var payload = parsed.TryGetValue("payload", out var raw) && raw is Dictionary<string, object> map
                ? map : new Dictionary<string, object>();

            return new AtlasLink(payload, Text(parsed, "path"), null,
                Text(parsed, "channel"), Text(parsed, "campaign"), Text(parsed, "clickedAt"),
                true, Text(parsed, "match"));
        }

        /// <summary>URI activations. True when the URI was a visit or handoff URL.</summary>
        public static bool Handle(string uri)
        {
            var link = LinkUrl.Parse(uri);

            if (link == null) return false;

            // The handoff form re-tapped after install: a deterministic claim.
            if (link.ClaimToken != null)
            {
                ClaimToken(link.ClaimToken, "relink");

                return true;
            }

            var core = Atlas.Core;

            if (core != null)
            {
                // The funnel's third step: an installed app, opened by the link.
                var payload = new Dictionary<string, object>
                {
                    ["eventId"] = AtlasCore.NewEventId(),
                    ["shortId"] = link.ShortId,
                };

                if (link.Channel != null) payload["channel"] = link.Channel;
                if (link.Campaign != null) payload["campaign"] = link.Campaign;
                if (link.PushId != null || link.Source == "push") payload["source"] = "push";

                core.Enqueue("open", payload);
            }

            // A direct open carries what the URL carries; the saved payload
            // rides only the deferred claim.
            Deliver(new AtlasLink(new Dictionary<string, object>(), null, link.ShortId,
                link.Channel, link.Campaign, null, false, null));

            return true;
        }

        /// <summary>The campaign id the store carried through the install
        /// (`cid=` on the visit page's store URL), handed in by the app.
        /// Ack-gated and once ever: replays are harmless, absence retries
        /// next launch.</summary>
        public static void ClaimCampaignId(string campaignId)
        {
            if (string.IsNullOrEmpty(campaignId)) return;

            ClaimToken(campaignId, "campaign_id");
        }

        private static void ClaimToken(string token, string via)
        {
            var core = Atlas.Core;

            if (core == null || ReadState("claim-done") != null) return;

            // Its own short thread: a claim must not sit in front of
            // envelope flushes on the core worker.
            var worker = new Thread(() =>
            {
                var outcome = new ClaimClient(core.BaseUrl).Claim(token, core.InstallId, via, out var link);

                if (outcome == ClaimOutcome.Retry) return;

                // Ack-gated: only a server answer ends the attempts.
                WriteState("claim-done", "1");

                if (link != null)
                {
                    StoreFirstLink(link);
                    Deliver(link);
                }
            }) {IsBackground = true, Name = "atlas-links-claim"};
            worker.Start();
        }

        private static void Deliver(AtlasLink link)
        {
            Action<AtlasLink> current;

            lock (Lock)
            {
                current = _listener;

                if (current == null)
                {
                    Queue.Add(link);

                    return;
                }
            }

            try
            {
                current(link);
            }
            catch (Exception)
            {
                // A listener's stumble is the app's bug, never the SDK's crash.
            }
        }

        private static void StoreFirstLink(AtlasLink link)
        {
            var stored = new Dictionary<string, object> {["payload"] = link.Payload};

            if (link.Path != null) stored["path"] = link.Path;
            if (link.Channel != null) stored["channel"] = link.Channel;
            if (link.Campaign != null) stored["campaign"] = link.Campaign;
            if (link.ClickedAt != null) stored["clickedAt"] = link.ClickedAt;
            if (link.Match != null) stored["match"] = link.Match;

            WriteState("first-link.json", Json.Write(stored));
        }

        private static string ReadState(string name)
        {
            try
            {
                var path = Path.Combine(Atlas.DataDir, name);

                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteState(string name, string value)
        {
            try
            {
                Directory.CreateDirectory(Atlas.DataDir);
                File.WriteAllText(Path.Combine(Atlas.DataDir, name), value);
            }
            catch (Exception)
            {
                // A disk that refuses costs a repeat claim; 409 absorbs it.
            }
        }

        private static string Text(Dictionary<string, object> parsed, string key)
        {
            return parsed.TryGetValue(key, out var value) ? value as string : null;
        }
    }
}
