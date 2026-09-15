using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AppAtlas.Sdk.Links
{
    /// <summary>
    /// Reads an incoming visit URL: the short id in its single path segment
    /// and the inflow keys the page also reads (ch, cp, src, nid) — plus the
    /// handoff form (/c/&lt;token&gt;), which carries a claim token instead.
    /// Host-agnostic on purpose.
    /// </summary>
    internal sealed class LinkUrl
    {
        // The server's alphabet (db/deep_links.py): 7 chars, no 0/O/1/l/I.
        private static readonly Regex ShortIdShape = new Regex("^[2-9A-HJ-NP-Za-km-z]{7}$", RegexOptions.Compiled);
        // The mint's shape: 16 hex chars, a dot, an unpadded-base64url signature.
        private static readonly Regex TokenShape = new Regex("^[0-9a-f]{16}\\.[A-Za-z0-9_-]{20,64}$", RegexOptions.Compiled);

        internal string ShortId { get; private set; }
        internal string Channel { get; private set; }
        internal string Campaign { get; private set; }
        internal string Source { get; private set; }
        internal string PushId { get; private set; }
        internal string ClaimToken { get; private set; }

        /// <summary>The parsed link, or null when the URL is neither shape.</summary>
        internal static LinkUrl Parse(string url)
        {
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;

            var segments = parsed.AbsolutePath.Trim('/').Split('/');

            if (segments.Length == 2 && segments[0] == "c" && TokenShape.IsMatch(segments[1]))
            {
                return new LinkUrl {ClaimToken = segments[1]};
            }

            if (segments.Length != 1 || !ShortIdShape.IsMatch(segments[0])) return null;

            var link = new LinkUrl {ShortId = segments[0]};
            var query = ParseQuery(parsed.Query);

            query.TryGetValue("ch", out var channel);
            query.TryGetValue("cp", out var campaign);
            query.TryGetValue("src", out var source);
            query.TryGetValue("nid", out var pushId);
            link.Channel = channel;
            link.Campaign = campaign;
            link.Source = source;
            link.PushId = pushId;

            return link;
        }

        private static Dictionary<string, string> ParseQuery(string raw)
        {
            var output = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(raw)) return output;

            foreach (var pair in raw.TrimStart('?').Split('&'))
            {
                var split = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(split == -1 ? pair : pair.Substring(0, split)).Replace('+', ' ');
                var value = split == -1 ? "" : Uri.UnescapeDataString(pair.Substring(split + 1).Replace('+', ' '));

                if (key.Length > 0 && !output.ContainsKey(key)) output[key] = value;
            }

            return output;
        }
    }
}
