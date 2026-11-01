using System;
using System.Collections.Generic;
using System.Text;

namespace DesultorySignals.Core.Diagnostics
{
    /// <summary>
    /// Log-safe transforms. Every logging path in this project routes through here,
    /// so "secrets do not reach the log" is a property of one file rather than a
    /// habit spread across twenty.
    /// </summary>
    /// <remarks>
    /// Two different problems are being solved:
    ///
    /// * Credentials must not be readable: a session id in a log is a session id
    ///   in a crash report, in a screenshot of the diagnostics page, and in
    ///   whatever the user pastes into a bug report.
    /// * Useful diagnostics must survive: "Cookie: &lt;redacted&gt;" is useless when
    ///   the question is whether the session cookie was present at all. So
    ///   redaction keeps the *shape* -- names, counts, lengths -- and drops only
    ///   the values.
    /// </remarks>
    public static class Redactor
    {
        /// <summary>Cookie names whose values are credentials.</summary>
        private static readonly HashSet<string> SensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sessionid", "sessionid_ss", "sessionid_ss_ads", "sid_tt", "sid_guard",
            "sid_ucp_v1", "ssid_ucp_v1", "uid_tt", "uid_tt_ss", "sso_uid_tt",
            "sso_uid_tt_ss", "odin_tt", "passport_csrf_token",
            "passport_csrf_token_default", "passport_auth_status",
            "passport_auth_status_ss", "d_ticket", "ticket", "login_status",
            "p_login_status", "store_idc", "store_region", "multi_sids", "n_mh",
            "s_v_web_id"
        };

        /// <summary>Query parameters whose values are credentials or long-lived keys.</summary>
        private static readonly HashSet<string> SensitiveParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "msToken", "ms_token", "verifyFp", "s_v_web_id", "ttwid", "uifid",
            "sessionid", "token", "password", "access_token"
        };

        public static bool IsSensitiveCookie(string name)
        {
            return !string.IsNullOrEmpty(name) && SensitiveNames.Contains(name);
        }

        /// <summary>A cookie value replaced by its length, or the value if it is not
        /// a credential.</summary>
        public static string CookieValue(string name, string value)
        {
            if (value == null) return null;
            if (IsSensitiveCookie(name)) return "<redacted:" + value.Length + ">";
            return value;
        }

        /// <summary>
        /// Turn a whole <c>Cookie</c> header into a safe summary: the names and the
        /// number of pairs, never the values of credentials.
        /// </summary>
        public static string CookieHeader(string header)
        {
            if (string.IsNullOrEmpty(header)) return "(no cookies)";

            var summary = new StringBuilder();
            int total = 0;
            int sensitive = 0;
            foreach (string chunk in header.Split(';'))
            {
                string item = chunk.Trim();
                if (item.Length == 0) continue;
                int equals = item.IndexOf('=');
                if (equals <= 0) continue;

                string name = item.Substring(0, equals).Trim();
                total++;
                bool isSensitive = IsSensitiveCookie(name);
                if (isSensitive) sensitive++;

                if (summary.Length > 0) summary.Append(", ");
                summary.Append(name);
                summary.Append(isSensitive ? "=<secret>" : "=<value>");
            }

            return string.Format("{0} cookies ({1} secret): {2}", total, sensitive, summary);
        }

        /// <summary>Replace sensitive query parameter values in a URL.</summary>
        public static string Url(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            int question = url.IndexOf('?');
            if (question < 0) return url;

            string prefix = url.Substring(0, question);
            string query = url.Substring(question + 1);
            var rebuilt = new StringBuilder(prefix);
            rebuilt.Append('?');

            string[] pairs = query.Split('&');
            for (int index = 0; index < pairs.Length; index++)
            {
                if (index > 0) rebuilt.Append('&');
                string pair = pairs[index];
                int equals = pair.IndexOf('=');
                if (equals <= 0)
                {
                    rebuilt.Append(pair);
                    continue;
                }
                string name = pair.Substring(0, equals);
                string value = pair.Substring(equals + 1);
                rebuilt.Append(name);
                rebuilt.Append('=');
                rebuilt.Append(SensitiveParameters.Contains(name)
                    ? "<redacted:" + value.Length + ">"
                    : value);
            }
            return rebuilt.ToString();
        }

        /// <summary>
        /// Last-resort scrub applied to any diagnostic string: catches a credential
        /// that reached the log through a path nobody anticipated (an exception
        /// message from a transport, for instance).
        /// </summary>
        public static string Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string scrubbed = text;

            foreach (string name in SensitiveNames)
            {
                scrubbed = ReplaceAssignment(scrubbed, name);
            }
            scrubbed = ReplaceAssignment(scrubbed, "msToken");
            scrubbed = ReplaceAssignment(scrubbed, "ttwid");
            scrubbed = ReplaceAssignment(scrubbed, "uifid");
            return scrubbed;
        }

        private static string ReplaceAssignment(string text, string name)
        {
            int index = 0;
            while (true)
            {
                int found = text.IndexOf(name + "=", index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return text;

                int valueStart = found + name.Length + 1;
                int valueEnd = valueStart;
                while (valueEnd < text.Length)
                {
                    char ch = text[valueEnd];
                    if (ch == ';' || ch == '&' || ch == ' ' || ch == '"' || ch == '\'' || ch == '\n' || ch == '\r')
                        break;
                    valueEnd++;
                }

                string value = text.Substring(valueStart, valueEnd - valueStart);
                if (value.Length == 0)
                {
                    index = valueStart;
                    continue;
                }

                text = text.Substring(0, valueStart) + "<redacted:" + value.Length + ">" + text.Substring(valueEnd);
                index = valueStart + 12;
            }
        }
    }
}
