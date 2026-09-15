using System.Collections.Generic;

namespace AppAtlas.Sdk.Links
{
    /// <summary>
    /// What the one listener receives, direct and deferred alike: the link's
    /// own payload plus how it arrived. One shape for both cases.
    /// </summary>
    public sealed class AtlasLink
    {
        /// <summary>The link's custom key-values, exactly as saved on the link.</summary>
        public IReadOnlyDictionary<string, object> Payload { get; }
        /// <summary>The deep-link path for this platform, when the link names one.</summary>
        public string Path { get; }
        public string ShortId { get; }
        public string Channel { get; }
        public string Campaign { get; }
        /// <summary>When the click that produced this link happened (ISO
        /// 8601); null on a direct open, which has no click behind it.</summary>
        public string ClickedAt { get; }
        /// <summary>True when this link survived an install (a claimed store handoff).</summary>
        public bool Deferred { get; }
        /// <summary>referrer, campaign_id, clipboard, relink — or null for a direct open.</summary>
        public string Match { get; }

        internal AtlasLink(IReadOnlyDictionary<string, object> payload, string path, string shortId,
            string channel, string campaign, string clickedAt, bool deferred, string match)
        {
            Payload = payload;
            Path = path;
            ShortId = shortId;
            Channel = channel;
            Campaign = campaign;
            ClickedAt = clickedAt;
            Deferred = deferred;
            Match = match;
        }
    }
}
