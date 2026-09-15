using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AppAtlas.Sdk
{
    /// <summary>
    /// The platform-free half of the SDK: an item goes to disk first as a
    /// one-item envelope, and a single background worker drains the queue —
    /// at start, and after every offer. Modules hand items in; they never
    /// touch the network themselves.
    /// </summary>
    public sealed class AtlasCore
    {
        internal const string Version = "0.1.0";

        private readonly string _sdkName;
        private readonly Dictionary<string, object> _context;
        private readonly DiskQueue _queue;
        private readonly Transport _transport;
        private readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();

        public string InstallId { get; }
        public string BaseUrl { get; }

        internal AtlasCore(string sdkName, string baseUrl, string sdkKey, string queueRoot, string installId,
            Dictionary<string, object> context)
        {
            _sdkName = sdkName;
            BaseUrl = baseUrl;
            InstallId = installId;
            _context = context;
            _queue = new DiskQueue(queueRoot);
            _transport = new Transport(baseUrl, sdkKey);

            var worker = new Thread(Run) {IsBackground = true, Name = "atlas-core"};
            worker.Start();

            // Adopt what dead instances left, then send it: the crash-era
            // envelope is the one that matters most.
            _work.Add(() =>
            {
                _queue.AdoptOrphans();
                Drain();
            });
        }

        /// <summary>A fresh id for one event: the server's idempotency handle.</summary>
        public static string NewEventId()
        {
            return Guid.NewGuid().ToString("D");
        }

        /// <summary>One item, disk-first, then the wire. Only the in-memory
        /// serialization happens on the caller's thread.</summary>
        public void Enqueue(string type, Dictionary<string, object> payload)
        {
            var envelope = new EnvelopeWriter(_sdkName, Version, IsoNow(), InstallId, _context)
                .Add(type, payload)
                .Bytes();

            _work.Add(() =>
            {
                _queue.Offer(envelope);
                Drain();
            });
        }

        /// <summary>Drain whatever the disk holds.</summary>
        public void FlushSoon()
        {
            _work.Add(Drain);
        }

        /// <summary>Blocks until queued work has run; for tests, never app code.</summary>
        public void AwaitIdle(int timeoutMs)
        {
            using (var idle = new ManualResetEventSlim(false))
            {
                _work.Add(() => idle.Set());
                idle.Wait(timeoutMs);
            }
        }

        private void Run()
        {
            foreach (var action in _work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception)
                {
                    // The worker outlives any single stumble; recording must
                    // never take the process with it.
                }
            }
        }

        private void Drain()
        {
            foreach (var path in _queue.List())
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (_transport.Limited(nowMs)) return;

                byte[] envelope;

                try
                {
                    envelope = File.ReadAllBytes(path);
                }
                catch (IOException)
                {
                    envelope = null;
                }

                if (envelope == null || envelope.Length == 0)
                {
                    // Unreadable is undeliverable; keeping it would spin forever.
                    DiskQueue.TryDelete(path);
                    continue;
                }

                switch (_transport.Send(envelope, nowMs))
                {
                    case TransportVerdict.Delivered:
                    case TransportVerdict.Refused:
                        DiskQueue.TryDelete(path);
                        break;
                    case TransportVerdict.RetryLater:
                        // The queue is ordered; if the head cannot go, the
                        // rest cannot either.
                        return;
                }
            }
        }

        private static string IsoNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
    }
}
