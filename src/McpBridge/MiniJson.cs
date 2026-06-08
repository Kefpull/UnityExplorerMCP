using System.Globalization;
using System.Text;

namespace UnityExplorer.McpBridge
{
    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            return new Parser(json).ParseValue();
        }

        public static string Serialize(object value)
        {
            StringBuilder builder = new();
            WriteValue(builder, value);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            if (value is string str)
            {
                WriteString(builder, str);
                return;
            }

            if (value is bool boolean)
            {
                builder.Append(boolean ? "true" : "false");
                return;
            }

            if (value is IDictionary<string, object> dict)
            {
                bool first = true;
                builder.Append('{');
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    WriteString(builder, pair.Key);
                    builder.Append(':');
                    WriteValue(builder, pair.Value);
                }
                builder.Append('}');
                return;
            }

            if (value is System.Collections.IDictionary rawDict)
            {
                bool first = true;
                builder.Append('{');
                foreach (System.Collections.DictionaryEntry pair in rawDict)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    WriteString(builder, pair.Key?.ToString() ?? "");
                    builder.Append(':');
                    WriteValue(builder, pair.Value);
                }
                builder.Append('}');
                return;
            }

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                bool first = true;
                builder.Append('[');
                foreach (object item in enumerable)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    WriteValue(builder, item);
                }
                builder.Append(']');
                return;
            }

            if (value is float f)
            {
                builder.Append(f.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is double d)
            {
                builder.Append(d.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal m)
            {
                builder.Append(m.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is IFormattable formattable)
            {
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            WriteString(builder, value.ToString());
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 32)
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }

        private sealed class Parser
        {
            private readonly string json;
            private int index;

            internal Parser(string json)
            {
                this.json = json;
            }

            internal object ParseValue()
            {
                SkipWhitespace();
                if (index >= json.Length)
                    return null;

                char c = json[index];
                if (c == '"')
                    return ParseString();
                if (c == '{')
                    return ParseObject();
                if (c == '[')
                    return ParseArray();
                if (c == 't' || c == 'f')
                    return ParseBool();
                if (c == 'n')
                    return ParseNull();

                return ParseNumber();
            }

            private Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> dict = new();
                index++;
                SkipWhitespace();

                if (TryConsume('}'))
                    return dict;

                while (index < json.Length)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    Consume(':');
                    object value = ParseValue();
                    dict[key] = value;
                    SkipWhitespace();

                    if (TryConsume('}'))
                        break;
                    Consume(',');
                }

                return dict;
            }

            private List<object> ParseArray()
            {
                List<object> list = new();
                index++;
                SkipWhitespace();

                if (TryConsume(']'))
                    return list;

                while (index < json.Length)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (TryConsume(']'))
                        break;
                    Consume(',');
                }

                return list;
            }

            private string ParseString()
            {
                Consume('"');
                StringBuilder builder = new();

                while (index < json.Length)
                {
                    char c = json[index++];
                    if (c == '"')
                        break;

                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (index >= json.Length)
                        break;

                    char escaped = json[index++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (index + 4 <= json.Length)
                            {
                                string hex = json.Substring(index, 4);
                                builder.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                index += 4;
                            }
                            break;
                    }
                }

                return builder.ToString();
            }

            private object ParseNumber()
            {
                int start = index;
                while (index < json.Length)
                {
                    char c = json[index];
                    if (!char.IsDigit(c) && c != '-' && c != '+' && c != '.' && c != 'e' && c != 'E')
                        break;
                    index++;
                }

                string text = json.Substring(start, index - start);
                if (text.Contains(".") || text.Contains("e") || text.Contains("E"))
                {
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        return d;
                }
                else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                {
                    if (l <= int.MaxValue && l >= int.MinValue)
                        return (int)l;
                    return l;
                }

                return 0;
            }

            private bool ParseBool()
            {
                if (json.Substring(index).StartsWith("true"))
                {
                    index += 4;
                    return true;
                }

                index += 5;
                return false;
            }

            private object ParseNull()
            {
                index += 4;
                return null;
            }

            private void SkipWhitespace()
            {
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                    index++;
            }

            private void Consume(char expected)
            {
                SkipWhitespace();
                if (index >= json.Length || json[index] != expected)
                    throw new FormatException($"Expected '{expected}' at JSON index {index}.");
                index++;
            }

            private bool TryConsume(char expected)
            {
                SkipWhitespace();
                if (index < json.Length && json[index] == expected)
                {
                    index++;
                    return true;
                }
                return false;
            }
        }
    }
}
