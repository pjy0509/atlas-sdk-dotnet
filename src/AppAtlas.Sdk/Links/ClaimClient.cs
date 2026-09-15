using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace AppAtlas.Sdk.Links
{
    internal enum ClaimOutcome
    {
        /// <summary>Claimed, already claimed, or never ours: the attempts end.</summary>
        Done,
        /// <summary>Offline or a server stumble: ask again next launch.</summary>
        Retry,
    }

    /// <summary>
    /// Exchanges a click token for the link it came from. The consumption
    /// rule: nothing is marked done until the server has answered — a claim
    /// lost to a dead process replays next launch, and the server's 409
    /// makes the replay harmless.
    /// </summary>
    internal sealed class ClaimClient
    {
        private static readonly HttpClient Client = new HttpClient {Timeout = TimeSpan.FromSeconds(10)};

        private readonly Uri _endpoint;

        internal ClaimClient(string baseUrl)
        {
            _endpoint = new Uri(baseUrl + "/api/ingest/link/claim");
        }

        /// <summary>Synchronous; only ever called off the caller's thread.
        /// `link` is set only on a 2xx with a body.</summary>
        internal ClaimOutcome Claim(string token, string installId, string via, out AtlasLink link)
        {
            link = null;

            var body = Json.Write(new Dictionary<string, object>
            {
                ["token"] = token, ["installId"] = installId, ["os"] = "windows", ["via"] = via,
            });

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (var answer = Client.SendAsync(request).GetAwaiter().GetResult())
                    {
                        var status = (int) answer.StatusCode;

                        if (status >= 200 && status < 300)
                        {
                            link = FromAnswer(answer.Content.ReadAsStringAsync().GetAwaiter().GetResult());

                            return ClaimOutcome.Done;
                        }

                        // Already claimed, expired, or never ours: over, quietly.
                        if (status == 404 || status == 409) return ClaimOutcome.Done;

                        return ClaimOutcome.Retry;
                    }
                }
            }
            catch (Exception)
            {
                return ClaimOutcome.Retry;
            }
        }

        private static AtlasLink FromAnswer(string answer)
        {
            var parsed = JsonReader.Object(answer);
            var payload = parsed.TryGetValue("payload", out var raw) && raw is Dictionary<string, object> map
                ? map : new Dictionary<string, object>();

            return new AtlasLink(payload, Text(parsed, "path"), null,
                Text(parsed, "channel"), Text(parsed, "campaign"), Text(parsed, "clickedAt"),
                true, Text(parsed, "match"));
        }

        private static string Text(Dictionary<string, object> parsed, string key)
        {
            return parsed.TryGetValue(key, out var value) ? value as string : null;
        }
    }
}
