using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Models
{
    /// <summary>
    /// Defensive readers for the normalized payloads the Python layer produces.
    /// </summary>
    /// <remarks>
    /// Every model in this project is populated by explicit reads rather than by
    /// reflection-based deserialization. Two reasons, both learned from the target:
    ///
    /// 1. .NET Native on Windows 10 Mobile resolves serializers through metadata
    ///    that has to be preserved with an rd.xml policy. Getting that policy wrong
    ///    fails at runtime on the device, not at build time. Explicit readers have
    ///    no metadata dependency at all.
    /// 2. The Python layer is the contract owner. When it renames a field, we want
    ///    that to be one obvious compile-time edit here rather than a silently
    ///    defaulted property.
    ///
    /// The readers are also tolerant by design: Douyin returns numbers as strings,
    /// flags as 1/0/true, and single values instead of arrays often enough that
    /// strict typing would be a liability.
    /// </remarks>
    public static class JsonValue
    {
        public static JObject AsObject(object value)
        {
            return value as JObject;
        }

        public static string GetString(JObject source, string name)
        {
            if (source == null) return null;
            JToken token = source[name];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return null;
            if (token.Type == JTokenType.String) return (string)token;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return token.ToString();
            if (token.Type == JTokenType.Boolean)
                return ((bool)token) ? "true" : "false";
            return null;
        }

        public static string GetString(JObject source, string name, string fallback)
        {
            string value = GetString(source, name);
            return value ?? fallback;
        }

        public static string GetFirstString(JObject source, params string[] names)
        {
            if (source == null) return null;
            for (int index = 0; index < names.Length; index++)
            {
                string value = GetString(source, names[index]);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        public static int? GetInt(JObject source, string name)
        {
            if (source == null) return null;
            JToken token = source[name];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return null;
            if (token.Type == JTokenType.Integer) return (int)(long)token;
            if (token.Type == JTokenType.Float)
            {
                double value = (double)token;
                if (value > int.MaxValue || value < int.MinValue) return null;
                return (int)value;
            }
            if (token.Type == JTokenType.String)
            {
                int parsed;
                if (int.TryParse((string)token, out parsed)) return parsed;
                return null;
            }
            if (token.Type == JTokenType.Boolean) return ((bool)token) ? 1 : 0;
            return null;
        }

        public static int GetInt(JObject source, string name, int fallback)
        {
            int? value = GetInt(source, name);
            return value.HasValue ? value.Value : fallback;
        }

        public static long? GetLong(JObject source, string name)
        {
            if (source == null) return null;
            JToken token = source[name];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return null;
            if (token.Type == JTokenType.Integer) return (long)token;
            if (token.Type == JTokenType.Float) return (long)(double)token;
            if (token.Type == JTokenType.String)
            {
                long parsed;
                if (long.TryParse((string)token, out parsed)) return parsed;
                return null;
            }
            return null;
        }

        public static bool GetBool(JObject source, string name, bool fallback)
        {
            if (source == null) return fallback;
            JToken token = source[name];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return fallback;
            if (token.Type == JTokenType.Boolean) return (bool)token;
            if (token.Type == JTokenType.Integer) return ((long)token) != 0;
            if (token.Type == JTokenType.String)
            {
                string text = ((string)token).Trim();
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                if (text == "1") return true;
                if (text == "0") return false;
            }
            return fallback;
        }

        public static IList<JObject> GetObjects(JObject source, string name)
        {
            var result = new List<JObject>();
            if (source == null) return result;
            JToken token = source[name];
            JArray array = token as JArray;
            if (array == null) return result;
            for (int index = 0; index < array.Count; index++)
            {
                JObject item = array[index] as JObject;
                if (item != null) result.Add(item);
            }
            return result;
        }

        public static IList<string> GetStrings(JObject source, string name)
        {
            var result = new List<string>();
            if (source == null) return result;
            JToken token = source[name];
            var array = token as JArray;
            if (array != null)
            {
                for (int index = 0; index < array.Count; index++)
                {
                    JToken item = array[index];
                    if (item != null && item.Type != JTokenType.Null)
                        result.Add(item.ToString());
                }
                return result;
            }
            if (token != null && token.Type == JTokenType.String)
            {
                string single = (string)token;
                if (!string.IsNullOrEmpty(single)) result.Add(single);
            }
            return result;
        }

        /// <summary>Absolute http(s) URL, or null. Guards against non-URL strings.</summary>
        public static string GetUrl(JObject source, string name)
        {
            string value = GetString(source, name);
            if (string.IsNullOrEmpty(value)) return null;
            Uri parsed;
            if (!Uri.TryCreate(value, UriKind.Absolute, out parsed)) return null;
            // netstandard1.4 has no Uri.UriSchemeHttp constant, so compare the scheme
            // textually. Case-insensitive because some CDNs answer with "HTTP://".
            string scheme = parsed.Scheme;
            if (!string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return value;
        }
    }
}
