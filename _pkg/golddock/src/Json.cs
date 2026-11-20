using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GoldDock
{
    /// <summary>Minimal recursive-descent JSON parser (C# 5 compatible).</summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("empty json");
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && At(s, i, "true")) { i += 4; return true; }
            if (c == 'f' && At(s, i, "false")) { i += 5; return false; }
            if (c == 'n' && At(s, i, "null")) { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        static bool At(string s, int i, string lit)
        {
            return i + lit.Length <= s.Length && string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string k = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expect :");
                i++;
                object v = ParseValue(s, ref i);
                d[k] = v;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new FormatException("expect , or }");
            }
            return d;
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var a = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new FormatException("expect , or ]");
            }
            return a;
        }

        static string ParseString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("expect string");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
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
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            int cp;
                            if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                                sb.Append((char)cp);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i == start) throw new FormatException("bad number at " + start);
            double d;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("bad number");
            return d;
        }

        // ---- typed helpers -------------------------------------------------

        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static List<object> Arr(object o) { return o as List<object>; }

        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null || key == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static Dictionary<string, object> ObjAt(object o, string key)
        {
            return Obj(Get(Obj(o), key));
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Dbl(Dictionary<string, object> d, string key, double def)
        {
            object v = Get(d, key);
            return ToDbl(v, def);
        }

        public static double ToDbl(object v, double def)
        {
            if (v == null) return def;
            if (v is double) return (double)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s == null) return def;
            s = s.Trim();
            if (s.Length == 0) return def;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return def;
        }
    }
}
