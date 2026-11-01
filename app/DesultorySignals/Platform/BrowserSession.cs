using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace DesultorySignals.Platform
{
    // UWP WebView and Windows.Web.Http use the app's cookie store. Cookies are
    // transferred explicitly after login; the API transport remains NoCookies.
    internal static class BrowserSession
    {
        private static readonly string[] Hosts = { "www.douyin.com", "login.douyin.com", "sso.douyin.com" };
        public static JArray Read()
        {
            var result = new JArray(); var seen = new HashSet<string>();
            using (var filter = new HttpBaseProtocolFilter())
                foreach (string host in Hosts)
                    foreach (HttpCookie cookie in filter.CookieManager.GetCookies(new Uri("https://" + host + "/")))
                    {
                        string key = cookie.Domain + "|" + cookie.Path + "|" + cookie.Name;
                        if (!seen.Add(key)) continue;
                        result.Add(new JObject {
                            { "name", cookie.Name }, { "value", cookie.Value }, { "domain", cookie.Domain },
                            { "path", cookie.Path }, { "secure", cookie.Secure }, { "httpOnly", cookie.HttpOnly },
                            { "expiresAtS", cookie.Expires.HasValue ? (JToken)new JValue((long)(cookie.Expires.Value.ToUniversalTime() - new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds) : JValue.CreateNull() }
                        });
                    }
            return result;
        }
        public static bool HasSession(JArray cookies)
        {
            foreach (JObject cookie in cookies)
            {
                string name = (string)cookie["name"];
                if ((name == "sessionid" || name == "sessionid_ss" || name == "sid_tt" || name == "sid_guard")
                    && !string.IsNullOrEmpty((string)cookie["value"])) return true;
            }
            return false;
        }
        public static void Clear()
        {
            using (var filter = new HttpBaseProtocolFilter())
                foreach (string host in Hosts)
                    foreach (HttpCookie cookie in filter.CookieManager.GetCookies(new Uri("https://" + host + "/")))
                        filter.CookieManager.DeleteCookie(cookie);
        }
    }
}
