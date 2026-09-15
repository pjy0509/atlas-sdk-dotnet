using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AppAtlas.Sdk
{
    /// <summary>
    /// A minimal JSON writer, so the core owns its bytes with zero
    /// dependencies and identical output on every platform the envelope
    /// fixtures check. Values are string, numbers, bool, IDictionary,
    /// IEnumerable or null.
    /// </summary>
    internal static class Json
    {
        internal static string Write(object value)
        {
            var output = new StringBuilder();
            Append(output, value);

            return output.ToString();
        }

        private static void Append(StringBuilder output, object value)
        {
            if (value == null)
            {
                output.Append("null");
            }
            else if (value is string text)
            {
                AppendString(output, text);
            }
            else if (value is bool flag)
            {
                output.Append(flag ? "true" : "false");
            }
            else if (value is int || value is long)
            {
                output.Append(((IFormattable) value).ToString(null, CultureInfo.InvariantCulture));
            }
            else if (value is double number)
            {
                // JSON has no NaN/Infinity; a broken measurement becomes null.
                output.Append(double.IsNaN(number) || double.IsInfinity(number)
                    ? "null" : number.ToString(CultureInfo.InvariantCulture));
            }
            else if (value is IDictionary map)
            {
                AppendObject(output, map);
            }
            else if (value is IEnumerable items)
            {
                AppendArray(output, items);
            }
            else
            {
                AppendString(output, value.ToString());
            }
        }

        private static void AppendObject(StringBuilder output, IDictionary map)
        {
            output.Append('{');
            var first = true;

            foreach (DictionaryEntry entry in map)
            {
                if (!first) output.Append(',');
                first = false;
                AppendString(output, entry.Key.ToString());
                output.Append(':');
                Append(output, entry.Value);
            }

            output.Append('}');
        }

        private static void AppendArray(StringBuilder output, IEnumerable items)
        {
            output.Append('[');
            var first = true;

            foreach (var item in items)
            {
                if (!first) output.Append(',');
                first = false;
                Append(output, item);
            }

            output.Append(']');
        }

        private static void AppendString(StringBuilder output, string text)
        {
            output.Append('"');

            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            output.Append("\\u").Append(((int) c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            output.Append(c);
                        }

                        break;
                }
            }

            output.Append('"');
        }
    }

    /// <summary>
    /// The reading half: enough JSON to take a server answer apart. Values
    /// come back as Dictionary&lt;string, object&gt;, List&lt;object&gt;,
    /// string, long, double, bool or null; anything unparseable is null.
    /// </summary>
    internal sealed class JsonReader
    {
        private readonly string _text;
        private int _at;

        private JsonReader(string text)
        {
            _text = text;
        }

        internal static object Parse(string text)
        {
            if (text == null) return null;

            try
            {
                var reader = new JsonReader(text);
                var value = reader.Value();
                reader.Space();

                return reader._at == text.Length ? value : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>An answer that is not the expected shape reads as empty.</summary>
        internal static Dictionary<string, object> Object(string text)
        {
            return Parse(text) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private object Value()
        {
            Space();

            switch (_text[_at])
            {
                case '{': return ObjectValue();
                case '[': return ArrayValue();
                case '"': return StringValue();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default: return NumberValue();
            }
        }

        private Dictionary<string, object> ObjectValue()
        {
            var map = new Dictionary<string, object>();
            _at++;
            Space();

            if (_text[_at] == '}')
            {
                _at++;

                return map;
            }

            while (true)
            {
                Space();
                var key = StringValue();
                Space();
                Expect(":");
                map[key] = Value();
                Space();

                var next = _text[_at++];

                if (next == '}') return map;
                if (next != ',') throw new System.InvalidOperationException("expected , or }");
            }
        }

        private List<object> ArrayValue()
        {
            var list = new List<object>();
            _at++;
            Space();

            if (_text[_at] == ']')
            {
                _at++;

                return list;
            }

            while (true)
            {
                list.Add(Value());
                Space();

                var next = _text[_at++];

                if (next == ']') return list;
                if (next != ',') throw new System.InvalidOperationException("expected , or ]");
            }
        }

        private string StringValue()
        {
            Expect("\"");

            var output = new StringBuilder();

            while (true)
            {
                var c = _text[_at++];

                if (c == '"') return output.ToString();

                if (c != '\\')
                {
                    output.Append(c);
                    continue;
                }

                var escape = _text[_at++];

                switch (escape)
                {
                    case '"': output.Append('"'); break;
                    case '\\': output.Append('\\'); break;
                    case '/': output.Append('/'); break;
                    case 'n': output.Append('\n'); break;
                    case 'r': output.Append('\r'); break;
                    case 't': output.Append('\t'); break;
                    case 'b': output.Append('\b'); break;
                    case 'f': output.Append('\f'); break;
                    case 'u':
                        output.Append((char) int.Parse(_text.Substring(_at, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        _at += 4;
                        break;
                    default: throw new System.InvalidOperationException("bad escape");
                }
            }
        }

        private object NumberValue()
        {
            var start = _at;

            while (_at < _text.Length && "+-0123456789.eE".IndexOf(_text[_at]) != -1) _at++;

            var slice = _text.Substring(start, _at - start);

            if (slice.IndexOf('.') == -1 && slice.IndexOf('e') == -1 && slice.IndexOf('E') == -1)
            {
                return long.Parse(slice, CultureInfo.InvariantCulture);
            }

            return double.Parse(slice, CultureInfo.InvariantCulture);
        }

        private void Expect(string literal)
        {
            if (string.CompareOrdinal(_text, _at, literal, 0, literal.Length) != 0)
            {
                throw new System.InvalidOperationException("expected " + literal);
            }

            _at += literal.Length;
        }

        private void Space()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at])) _at++;
        }
    }
}
