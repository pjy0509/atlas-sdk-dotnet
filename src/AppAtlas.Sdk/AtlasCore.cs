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
        internal const string Version = "0.4.0";

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
            Batch().Add(type, payload).Enqueue();
        }

        /// <summary>Several items that must arrive together: a crash and its
        /// session's end.</summary>
        public EnvelopeBatch Batch()
        {
            return new EnvelopeBatch(this, new EnvelopeWriter(_sdkName, Version, IsoNow(), InstallId, _context));
        }

        /// <summary>One envelope holding several items, sent together or not at all.</summary>
        public sealed class EnvelopeBatch
        {
            private readonly AtlasCore _core;
            private readonly EnvelopeWriter _writer;

            internal EnvelopeBatch(AtlasCore core, EnvelopeWriter writer)
            {
                _core = core;
                _writer = writer;
            }

            public EnvelopeBatch Add(string type, Dictionary<string, object> payload)
            {
                _writer.Add(type, payload);

                return this;
            }

            /// <summary>Disk on the worker, then the wire.</summary>
            public void Enqueue()
            {
                var envelope = _writer.Bytes();

                _core._work.Add(() =>
                {
                    _core._queue.Offer(envelope);
                    _core.Drain();
                });
            }

            /// <summary>Disk on the caller's thread, synchronously, then the
            /// wire from the worker. For a thread whose process is about to
            /// die. False when the disk refused.</summary>
            public bool PersistNow()
            {
                var written = _core._queue.Offer(_writer.Bytes()) != null;

                if (written) _core.FlushSoon();

                return written;
            }
        }

        /// <summary>Drain whatever the disk holds.</summary>
        public void FlushSoon()
        {
            _work.Add(Drain);
        }

        /// <summary>Drain now and wait for it, up to the timeout: the last
        /// thing a terminating process does, and the launch-crash fast path.</summary>
        public void FlushWithin(int timeoutMs)
        {
            if (timeoutMs <= 0) return;

            using (var done = new ManualResetEventSlim(false))
            {
                _work.Add(() =>
                {
                    Drain();
                    done.Set();
                });
                done.Wait(timeoutMs);
            }
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

        /// <summary>The wire's instant format, UTC to the second.</summary>
        public static string IsoNow()
        {
            return Iso(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public static string Iso(long epochMs)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
    }
}
