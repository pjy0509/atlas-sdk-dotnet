using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AppAtlas.Sdk
{
    /// <summary>
    /// Builds the wire bytes the server's parser reads (common/envelope.py)
    /// and tests/envelope_cases.json pins for every SDK. Every item declares
    /// its byte length.
    /// </summary>
    internal sealed class EnvelopeWriter
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private readonly MemoryStream _output = new MemoryStream();

        /// <summary>`context` rides the header once per envelope — the device
        /// and app facts every item shares (the crash module reads the same
        /// block). May be null.</summary>
        internal EnvelopeWriter(string sdkName, string sdkVersion, string sentAtIso, string installId,
            Dictionary<string, object> context)
        {
            var header = new Dictionary<string, object>
            {
                ["sdk"] = new Dictionary<string, object> {["name"] = sdkName, ["version"] = sdkVersion},
                ["sentAt"] = sentAtIso,
                ["installId"] = installId,
            };

            if (context != null)
            {
                foreach (var entry in context) header[entry.Key] = entry.Value;
            }

            Line(Json.Write(header));
        }

        /// <summary>One item: a type-plus-length header line, then the payload bytes.</summary>
        internal EnvelopeWriter Add(string type, Dictionary<string, object> payload)
        {
            var body = Utf8.GetBytes(Json.Write(payload));

            Line(Json.Write(new Dictionary<string, object> {["type"] = type, ["length"] = body.Length}));
            _output.Write(body, 0, body.Length);
            _output.WriteByte((byte) '\n');

            return this;
        }

        internal byte[] Bytes()
        {
            return _output.ToArray();
        }

        private void Line(string text)
        {
            var bytes = Utf8.GetBytes(text);
            _output.Write(bytes, 0, bytes.Length);
            _output.WriteByte((byte) '\n');
        }
    }
}
