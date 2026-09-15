using System;
using System.Globalization;
using System.Net.Http;

namespace AppAtlas.Sdk
{
    internal enum TransportVerdict
    {
        /// <summary>The server took it: delete the file.</summary>
        Delivered,
        /// <summary>The server will never take it (4xx): delete; retrying is spam.</summary>
        Refused,
        /// <summary>Offline, 5xx, or an active rate limit: keep for later.</summary>
        RetryLater,
    }

    /// <summary>
    /// One envelope over the wire, synchronously — only ever called on the
    /// core's worker thread. While a 429's Retry-After holds, sends are
    /// skipped entirely: buffering during a limit is how one 429 becomes a
    /// flood.
    /// </summary>
    internal sealed class Transport
    {
        private static readonly HttpClient Client = new HttpClient {Timeout = TimeSpan.FromSeconds(10)};

        private readonly Uri _endpoint;
        private readonly string _sdkKey;
        private long _retryNotBeforeMs;

        internal Transport(string baseUrl, string sdkKey)
        {
            _endpoint = new Uri(baseUrl + "/api/ingest/envelope");
            _sdkKey = sdkKey;
        }

        internal bool Limited(long nowMs)
        {
            return nowMs < _retryNotBeforeMs;
        }

        internal TransportVerdict Send(byte[] envelope, long nowMs)
        {
            if (Limited(nowMs)) return TransportVerdict.RetryLater;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _sdkKey);
                    request.Content = new ByteArrayContent(envelope);
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-atlas-envelope");

                    // Sync-over-async is safe here by construction: this only
                    // runs on the dedicated worker thread, never a UI context.
                    using (var answer = Client.SendAsync(request).GetAwaiter().GetResult())
                    {
                        var status = (int) answer.StatusCode;

                        if (status == 429)
                        {
                            _retryNotBeforeMs = nowMs + RetryAfterMs(answer);

                            return TransportVerdict.RetryLater;
                        }

                        if (status >= 200 && status < 300) return TransportVerdict.Delivered;
                        if (status >= 400 && status < 500) return TransportVerdict.Refused;

                        return TransportVerdict.RetryLater;
                    }
                }
            }
            catch (Exception)
            {
                // Offline, DNS, TLS: all the same answer — later.
                return TransportVerdict.RetryLater;
            }
        }

        private static long RetryAfterMs(HttpResponseMessage answer)
        {
            if (answer.Headers.TryGetValues("Retry-After", out var values))
            {
                foreach (var value in values)
                {
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                        && seconds > 0)
                    {
                        return seconds * 1000L;
                    }
                }
            }

            return 60_000L;
        }
    }
}
