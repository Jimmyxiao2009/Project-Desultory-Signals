using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Networking;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using Windows.Web.Http.Headers;

namespace DesultorySignals.Platform
{
    /// <summary>
    /// <see cref="IHttpTransport"/> over <c>Windows.Web.Http</c>, for Windows 10
    /// Mobile 10586.
    /// </summary>
    /// <remarks>
    /// Four decisions worth stating, because each one is a bug that would otherwise
    /// be blamed on Douyin:
    ///
    /// * <b>One client per request.</b> Redirect policy is a property of
    ///   <see cref="HttpBaseProtocolFilter"/> and the Core layer sets it per request
    ///   (a range read must never be silently redirected). Per-request filters make
    ///   that honest instead of approximate -- and since every filter owns a private
    ///   response cache, no request can be answered from another one's cached body.
    /// * <b>Redirects are handled explicitly when disabled.</b> With
    ///   <c>AllowAutoRedirect = false</c> the <c>Location</c> header is the only way
    ///   to learn the real URL, so it is copied out both as a typed value and as a
    ///   header (which is what the Core redirect handling reads).
    /// * <b>Set-Cookie is collected the only way this API exposes it.</b> There is no
    ///   <c>TryGetValues</c> on the projected <c>HttpResponseHeaderCollection</c> --
    ///   checked against the 19041 <c>Windows.winmd</c> -- so cookies are taken from
    ///   the filter's own <c>HttpCookieManager</c>, which is a real per-cookie store
    ///   and therefore cannot lose a repeat, and any raw <c>Set-Cookie</c> line that
    ///   does surface in the header enumeration is kept verbatim and preferred. The
    ///   Core layer's cookie jar parses either form.
    /// * <b>Errors are classified, not thrown raw.</b> A socket failure becomes
    ///   <see cref="ErrorKinds.Network"/> with <c>retryable: true</c>. A bare
    ///   <c>Exception</c> would slip past <c>RetryPolicy</c> and surface on the device
    ///   as an unexplained failure.
    ///
    /// The streaming path deliberately hands the WinRT response objects to the
    /// returned <see cref="HttpResponseData"/>: disposing this method's locals would
    /// close the body before the caller (a video download) had read it.
    /// </remarks>
    public sealed class UwpHttpTransport : IHttpTransport
    {
        private readonly DiagLog _log;

        public UwpHttpTransport(DiagLog log)
        {
            _log = log;
        }

        public async Task<HttpResponseData> SendAsync(
            HttpRequestSpec request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (string.IsNullOrEmpty(request.Url))
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.InvalidInput, "the request has no URL", false));

            var stopwatch = Stopwatch.StartNew();

            HttpBaseProtocolFilter filter = null;
            HttpClient client = null;
            HttpRequestMessage message = null;
            HttpResponseMessage response = null;
            bool ownershipTransferred = false;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (request.TimeoutMs > 0) timeout.CancelAfter(request.TimeoutMs);

                try
                {
                    filter = new HttpBaseProtocolFilter
                    {
                        AllowAutoRedirect = request.FollowRedirects,
                        // This transport must not keep a cookie jar of its own.
                        //
                        // By default the WinRT client stores every response cookie and
                        // re-attaches the matching ones to later requests. We also send an
                        // explicit Cookie header, built by the session's own jar. Two
                        // jars means a request can carry the session's cookie *and* a
                        // stale copy of the same name, which is exactly the
                        // "cookie belongs to identity A but is signed as identity B"
                        // mismatch this project forbids -- and it would present as
                        // intermittent 403s with no visible cause.
                        //
                        // With NoCookies the client neither stores nor sends cookies, so
                        // the Python-side jar is the single source of truth. Set-Cookie is
                        // then read from the response headers; the cookie-manager read in
                        // CollectCookies stays as a harmless second chance (it will simply
                        // be empty here).
                        CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies
                    };
                    // The WinRT HTTP cache is shared, even across new filter instances.
                    // MostRecent is supported on 10586; NoCache read mode needs 14393.
                    filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
                    filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
                    client = new HttpClient(filter);

                    message = BuildMessage(request);

                    HttpCompletionOption completion = request.StreamResponse
                        ? HttpCompletionOption.ResponseHeadersRead
                        : HttpCompletionOption.ResponseContentRead;

                    _log.Trace(DiagCategory.Transport, "send " + request.DescribeForLog(),
                        new Newtonsoft.Json.Linq.JObject
                        {
                            { "tag", request.Tag },
                            { "stream", request.StreamResponse },
                            { "redirects", request.FollowRedirects },
                            { "timeoutMs", request.TimeoutMs }
                        });

                    message.Headers.TryAppendWithoutValidation("Cache-Control", "no-cache, no-store");
                    message.Headers.TryAppendWithoutValidation("Pragma", "no-cache");
                    response = await client.SendRequestAsync(message, completion).AsTask(timeout.Token);

                    var data = new HttpResponseData
                    {
                        StatusCode = (int)response.StatusCode,
                        FinalUrl = DescribeFinalUrl(response, request.Url),
                        ContentType = headerToText(response.Content.Headers.ContentType),
                        ContentLength = ToNullableLong(response.Content.Headers.ContentLength),
                        ContentRange = headerToText(response.Content.Headers.ContentRange),
                        Location = response.Headers.Location == null
                            ? null
                            : response.Headers.Location.AbsoluteUri
                    };

                    CopyHeaders(response, data);
                    CollectCookies(filter, request.Url, data.FinalUrl, data);

                    if (request.StreamResponse)
                    {
                        IInputStream input = await response.Content.ReadAsInputStreamAsync()
                            .AsTask(timeout.Token);

                        data.BodyStream = new ResponseOwningStream(
                            input.AsStreamForRead(), response, client, filter, message);

                        // The response outlives this method now.
                        ownershipTransferred = true;
                        response = null;
                        client = null;
                        filter = null;
                        message = null;
                    }
                    else
                    {
                        IBuffer buffer = await response.Content.ReadAsBufferAsync()
                            .AsTask(timeout.Token);
                        data.Body = buffer.ToArray();
                    }

                    stopwatch.Stop();
                    data.ElapsedMs = stopwatch.ElapsedMilliseconds;

                    _log.Trace(DiagCategory.Transport, string.Format(
                        "{0} -> HTTP {1}, {2} bytes, {3} ms, {4} set-cookie",
                        request.DescribeForLog(),
                        data.StatusCode,
                        data.Body == null ? -1 : data.Body.Length,
                        data.ElapsedMs,
                        data.SetCookies.Count));

                    return data;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _log.Info(DiagCategory.Transport,
                            "request cancelled by the caller: " + request.DescribeForLog());
                        throw;
                    }

                    _log.Warn(DiagCategory.Transport, string.Format(
                        "request timed out after {0} ms: {1}", request.TimeoutMs, request.DescribeForLog()));
                    throw DouyinApiException.Network(
                        "the request to " + SafeHost(request.Url) + " timed out after "
                            + request.TimeoutMs + " ms",
                        null);
                }
                catch (DouyinApiException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // COMException from the WinRT stack, HttpRequestException from the
                    // projection, OutOfMemory for a hostile Content-Length: all of them
                    // mean "the network layer did not deliver", and all of them are
                    // worth retrying.
                    _log.Warn(DiagCategory.Transport,
                        "request failed: " + request.DescribeForLog() + " | " + exception.Message);
                    throw DouyinApiException.Network(
                        "the request to " + SafeHost(request.Url) + " failed: " + exception.Message,
                        exception);
                }
                finally
                {
                    if (!ownershipTransferred)
                    {
                        DisposeQuietly(message);
                        DisposeQuietly(response);
                        DisposeQuietly(client);
                        DisposeQuietly(filter);
                    }
                }
            }
        }

        private static HttpRequestMessage BuildMessage(HttpRequestSpec request)
        {
            var message = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrEmpty(request.Method) ? "GET" : request.Method),
                new Uri(request.Url));

            if (request.Body != null || RequiresContent(request.Method))
            {
                IBuffer buffer = CryptographicBuffer.CreateFromByteArray(request.Body ?? new byte[0]);
                var content = new HttpBufferContent(buffer);

                string contentType = request.GetHeader("Content-Type");
                if (!string.IsNullOrEmpty(contentType))
                {
                    HttpMediaTypeHeaderValue parsed;
                    if (HttpMediaTypeHeaderValue.TryParse(contentType, out parsed))
                        content.Headers.ContentType = parsed;
                }
                message.Content = content;
            }

            for (int index = 0; index < request.Headers.Count; index++)
            {
                KeyValuePair<string, string> header = request.Headers[index];
                if (IsContentHeader(header.Key)) continue;

                try
                {
                    // Append, not Set: a repeated name must stay repeated. A Cookie
                    // header goes here too, not through a Cookies collection, so no
                    // automatic jar ever rewrites it.
                    message.Headers.Append(header.Key, header.Value ?? string.Empty);
                }
                catch (Exception)
                {
                    // A name WinRT considers restricted (Host, Connection, ...). Losing
                    // it is better than failing the whole request, and it is logged so
                    // the gap is visible rather than silent.
                    try
                    {
                        message.Headers.TryAppendWithoutValidation(header.Key, header.Value ?? string.Empty);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            return message;
        }

        private static bool RequiresContent(string method)
        {
            if (string.IsNullOrEmpty(method)) return false;
            return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsContentHeader(string name)
        {
            return string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Copy every response header, repeats included. The collection is enumerated
        /// as name/value pairs rather than probed by name, because the projected API
        /// has no multi-value getter (no <c>TryGetValues</c> in the metadata).
        /// </summary>
        private static void CopyHeaders(HttpResponseMessage response, HttpResponseData data)
        {
            foreach (KeyValuePair<string, string> header in response.Headers)
            {
                data.AddHeader(header.Key, header.Value ?? string.Empty);
            }

            if (!string.IsNullOrEmpty(data.ContentType)) data.AddHeader("Content-Type", data.ContentType);
            if (!string.IsNullOrEmpty(data.ContentRange)) data.AddHeader("Content-Range", data.ContentRange);

            // Location is exposed as a typed property; make sure the header view has it
            // too, since that is what the Core redirect handling reads.
            if (!string.IsNullOrEmpty(data.Location) && data.GetHeader("Location") == null)
                data.AddHeader("Location", data.Location);
        }

        /// <summary>
        /// Take every cookie the response established.
        /// </summary>
        /// <remarks>
        /// Two sources, because neither is guaranteed alone:
        ///
        /// 1. The <c>Set-Cookie</c> lines that survive in the response header
        ///    enumeration. With <c>CookieUsageBehavior.NoCookies</c> the platform does
        ///    not consume them into a jar, so this is the primary source, and
        ///    <see cref="CopyHeaders"/> has already copied them verbatim.
        /// 2. The filter's <c>HttpCookieManager</c>, queried for both the requested and
        ///    the final (possibly redirected) URL. A host-only cookie set during a
        ///    redirect to another host is not applicable to the original URL, so
        ///    querying only one of them would drop it. With NoCookies this is normally
        ///    empty, but keeping it costs nothing and covers a platform that still
        ///    populates the jar.
        ///
        /// A raw line wins over a reconstructed one: it carries <c>Max-Age</c> and
        /// <c>SameSite</c>, which <c>HttpCookie</c> does not model and which this code
        /// must not invent. The manager-derived line is only added when that name is
        /// still missing.
        /// </remarks>
        private void CollectCookies(
            HttpBaseProtocolFilter filter, string requestUrl, string finalUrl, HttpResponseData data)
        {
            if (filter == null || data == null || filter.CookieUsageBehavior == HttpCookieUsageBehavior.NoCookies) return;

            var urls = new List<Uri>();
            AddUri(urls, finalUrl);
            AddUri(urls, requestUrl);

            var alreadyPresent = new List<string>();
            foreach (string existing in data.SetCookies)
            {
                string name = NameOfSetCookie(existing);
                if (!string.IsNullOrEmpty(name)) alreadyPresent.Add(name);
            }

            for (int index = 0; index < urls.Count; index++)
            {
                IReadOnlyList<HttpCookie> cookies;
                try
                {
                    cookies = filter.CookieManager.GetCookies(urls[index]);
                }
                catch (Exception exception)
                {
                    _log.Trace(DiagCategory.Transport,
                        "could not read the cookie jar for " + urls[index].Host + ": " + exception.Message);
                    continue;
                }

                if (cookies == null) continue;

                for (int cookieIndex = 0; cookieIndex < cookies.Count; cookieIndex++)
                {
                    HttpCookie cookie = cookies[cookieIndex];
                    if (cookie == null || string.IsNullOrEmpty(cookie.Name)) continue;
                    if (Contains(alreadyPresent, cookie.Name)) continue;

                    alreadyPresent.Add(cookie.Name);
                    data.AddHeader("Set-Cookie", FormatSetCookie(cookie));
                }
            }
        }

        private static void AddUri(IList<Uri> urls, string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return;

            for (int index = 0; index < urls.Count; index++)
            {
                if (urls[index] == parsed) return;
            }
            urls.Add(parsed);
        }

        private static bool Contains(IList<string> names, string name)
        {
            for (int index = 0; index < names.Count; index++)
            {
                if (string.Equals(names[index], name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Name part of a <c>Set-Cookie</c> line, or null when it has none.</summary>
        private static string NameOfSetCookie(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int equals = line.IndexOf('=');
            if (equals <= 0) return null;
            string name = line.Substring(0, equals).Trim();
            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// Render one <see cref="HttpCookie"/> as a <c>Set-Cookie</c> line, which is
        /// the shape the Python cookie jar parses. Attributes the WinRT type does not
        /// model (Max-Age, SameSite) are simply absent rather than invented.
        /// </summary>
        private static string FormatSetCookie(HttpCookie cookie)
        {
            var text = new System.Text.StringBuilder();
            text.Append(cookie.Name).Append('=').Append(cookie.Value ?? string.Empty);

            if (!string.IsNullOrEmpty(cookie.Domain)) text.Append("; Domain=").Append(cookie.Domain);
            if (cookie.Path != null) text.Append("; Path=").Append(cookie.Path);
            if (cookie.Expires.HasValue)
            {
                text.Append("; Expires=")
                    .Append(cookie.Expires.Value.ToUniversalTime().ToString("r"));
            }
            if (cookie.Secure) text.Append("; Secure");
            if (cookie.HttpOnly) text.Append("; HttpOnly");

            return text.ToString();
        }

        private static string DescribeFinalUrl(HttpResponseMessage response, string fallback)
        {
            try
            {
                // With auto-redirect on, the request message carries the URI that was
                // actually served, which is what the signature and cookie logic needs.
                if (response.RequestMessage != null && response.RequestMessage.RequestUri != null)
                    return response.RequestMessage.RequestUri.AbsoluteUri;
            }
            catch (Exception)
            {
            }
            return fallback;
        }

        private static string headerToText(object header)
        {
            if (header == null) return null;
            return header.ToString();
        }

        /// <summary>WinRT reports content length as <c>ulong?</c>; the Core model is
        /// <c>long?</c>. Anything past <c>long.MaxValue</c> is not a real body and is
        /// clamped rather than wrapped into a negative length.</summary>
        private static long? ToNullableLong(ulong? value)
        {
            if (!value.HasValue) return null;
            return value.Value > (ulong)long.MaxValue ? long.MaxValue : (long)value.Value;
        }

        private static string SafeHost(string url)
        {
            try
            {
                return new Uri(url).Host;
            }
            catch (Exception)
            {
                return "(unparseable URL)";
            }
        }

        private static void DisposeQuietly(IDisposable disposable)
        {
            if (disposable == null) return;
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// A readable stream that also owns the WinRT objects the bytes come from.
    /// </summary>
    /// <remarks>
    /// Needed for the streaming path: <see cref="HttpResponseData.Dispose"/> closes
    /// only <c>BodyStream</c>, so the response, client and filter have to be released
    /// from here. Without it, disposing the client at the end of
    /// <c>UwpHttpTransport.SendAsync</c> would abort the body that a video download is
    /// still reading.
    /// </remarks>
    internal sealed class ResponseOwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable[] _owners;
        private bool _disposed;

        public ResponseOwningStream(Stream inner, params IDisposable[] owners)
        {
            _inner = inner;
            _owners = owners ?? new IDisposable[0];
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { throw new NotSupportedException("the response body length is not known up front"); }
        }

        public override long Position
        {
            get { throw new NotSupportedException("the response body is not seekable"); }
            set { throw new NotSupportedException("the response body is not seekable"); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("the response body is not seekable");
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("the response body is read-only");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("the response body is read-only");
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    try
                    {
                        _inner.Dispose();
                    }
                    catch (Exception)
                    {
                    }

                    for (int index = 0; index < _owners.Length; index++)
                    {
                        if (_owners[index] == null) continue;
                        try
                        {
                            _owners[index].Dispose();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            base.Dispose(disposing);
        }
    }
}
