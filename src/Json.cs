// Minimal JSON reader/writer (no external libraries). C# 5 only. From Phoenix LAI (MIT).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Windlass
{
    public static class Json
    {
        // Parses JSON into Dictionary<string, object>, List<object>, string, double, bool or null.
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("Trailing characters in JSON.");
            return v;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> o = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    string key = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("Expected ':' in JSON.");
                    i++;
                    o[key] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("Expected ',' or '}' in JSON.");
                }
            }
            if (c == '[')
            {
                List<object> a = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("Expected ',' or ']' in JSON.");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("Unexpected character in JSON.");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string in JSON.");
            i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated string in JSON.");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: throw new FormatException("Bad escape in JSON.");
                }
            }
        }

        public static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 2);
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
                        if (c < 0x20 || c == '<' || c == '>' || c == '&') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        // Writes a parsed value (Dictionary / List / string / double / bool / null) back as compact JSON.
        public static string Write(object v)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { sb.Append(Quote((string)v)); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double) { sb.Append(Num((double)v)); return; }
            if (v is int || v is long) { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            Dictionary<string, object> d = v as Dictionary<string, object>;
            if (d != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(kv.Key)).Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            List<object> l = v as List<object>;
            if (l != null)
            {
                sb.Append('[');
                for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); Write(sb, l[i]); }
                sb.Append(']');
                return;
            }
            sb.Append(Quote(v.ToString()));
        }

        // Typed getters with defaults.
        public static double GetNum(Dictionary<string, object> o, string key, double dflt)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) && v is double ? (double)v : dflt;
        }

        public static string GetStr(Dictionary<string, object> o, string key, string dflt)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) && v is string ? (string)v : dflt;
        }
    }
}
