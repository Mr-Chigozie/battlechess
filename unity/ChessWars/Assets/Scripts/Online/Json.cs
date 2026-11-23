// A small JSON reader and writer for talking to the server: objects become Dictionary<string, object>, arrays
// List<object>, numbers double, plus string, bool and null. Enough for the server's replies; no package needed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChessWars.Online
{
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            var value = Value(text, ref i);
            return value;
        }

        static void Space(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object Value(string s, ref int i)
        {
            Space(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var obj = new Dictionary<string, object>();
                i++;
                Space(s, ref i);
                if (s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Space(s, ref i);
                    string key = Str(s, ref i);
                    Space(s, ref i);
                    if (s[i++] != ':') throw new FormatException("Expected :");
                    obj[key] = Value(s, ref i);
                    Space(s, ref i);
                    char d = s[i++];
                    if (d == '}') return obj;
                    if (d != ',') throw new FormatException("Expected , or }");
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                i++;
                Space(s, ref i);
                if (s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i));
                    Space(s, ref i);
                    char d = s[i++];
                    if (d == ']') return list;
                    if (d != ',') throw new FormatException("Expected , or ]");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException($"Unexpected '{c}' in JSON");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected a string");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: Quote(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case char ch: Quote(sb, ch.ToString()); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> obj:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in obj)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Quote(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var x in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, x);
                    }
                    sb.Append(']');
                    break;
                }
                default: Quote(sb, v.ToString()); break;
            }
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>Friendly reading of a parsed JSON object: missing or mistyped fields give the fallback.</summary>
    public static class JsonExt
    {
        public static Dictionary<string, object> Obj(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        public static List<object> List(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is List<object> l ? l : new List<object>();
        public static string Str(this Dictionary<string, object> o, string key, string fallback = null) =>
            o != null && o.TryGetValue(key, out var v) && v != null ? (v is double d ? d.ToString(CultureInfo.InvariantCulture) : v.ToString()) : fallback;
        public static double Num(this Dictionary<string, object> o, string key, double fallback = 0) =>
            o != null && o.TryGetValue(key, out var v) && v is double d ? d : fallback;
        public static int Int(this Dictionary<string, object> o, string key, int fallback = 0) => (int)o.Num(key, fallback);
        public static long Long(this Dictionary<string, object> o, string key, long fallback = 0) => (long)o.Num(key, fallback);
        public static bool Bool(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is bool b && b;
        public static bool Has(this Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) && v != null;
    }
}
