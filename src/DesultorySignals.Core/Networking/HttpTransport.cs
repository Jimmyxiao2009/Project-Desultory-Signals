using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesultorySignals.Core.Networking
{
    /// <summary>
    /// One HTTP request, expressed without reference to any particular client
    /// library.
    /// </summary>
    /// <remarks>
    /// The Core layer deliberately does not use <c>System.Net.Http</c> even though
    /// the app can: it is not part of netstandard1.4, which is the target the core
    /// logic is built against so it can be unit tested on a desktop. Everything
    /// network-shaped therefore goes through this type and
    /// <see cref="IHttpTransport"/>, which the UWP app implements over
    /// <c>Windows.Web.Http</c>.
    ///
    /// Header order is preserved because the API is sensitive to it, and duplicate
    /// header names are allowed because <c>Set-Cookie</c> legitimately repeats.
    /// </remarks>
    public sealed class HttpRequestSpec
    {
        private readonly List<KeyValuePair<string, string>> _headers =
            new List<KeyValuePair<string, string>>();

        public string Method { get; set; }
        public string Url { get; set; }
        public byte[] Body { get; set; }

        /// <summary>When true the transport must not buffer the body; it exposes
        /// <see cref="HttpResponseData.BodyStream"/> instead.</summary>
        public bool StreamResponse { get; set; }

        /// <summary>Timeout for this request. Range reads use a short one so a stalled
        /// chunk cannot wedge playback.</summary>
        public int TimeoutMs { get; set; }

        /// <summary>Diagnostics label, e.g. "feed#2" or "range:chunk7".</summary>
        public string Tag { get; set; }

        /// <summary>Whether redirects should be followed. Range requests must not be
        /// redirected silently: a redirected range read can return the wrong bytes.</summary>
        public bool FollowRedirects { get; set; }

        public IList<KeyValuePair<string, string>> Headers
        {
            get { return _headers; }
        }

        public HttpRequestSpec()
        {
            Method = "GET";
            TimeoutMs = 20000;
            FollowRedirects = true;
        }

        public HttpRequestSpec AddHeader(string name, string value)
        {
            if (!string.IsNullOrEmpty(name) && value != null)
                _headers.Add(new KeyValuePair<string, string>(name, value));
            return this;
        }

        public HttpRequestSpec SetHeader(string name, string value)
        {
            for (int index = _headers.Count - 1; index >= 0; index--)
            {
                if (string.Equals(_headers[index].Key, name, StringComparison.OrdinalIgnoreCase))
                    _headers.RemoveAt(index);
            }
            return AddHeader(name, value);
        }

        public string GetHeader(string name)
        {
            for (int index = 0; index < _headers.Count; index++)
            {
                if (string.Equals(_headers[index].Key, name, StringComparison.OrdinalIgnoreCase))
                    return _headers[index].Value;
            }
            return null;
        }

        /// <summary>Set a byte range. Inclusive bounds, as the HTTP header defines them.</summary>
        public HttpRequestSpec SetRange(long start, long end)
        {
            if (start < 0) throw new ArgumentOutOfRangeException("start");
            if (end < start) throw new ArgumentOutOfRangeException("end");
            SetHeader("Range", "bytes=" + start + "-" + end);
            return this;
        }

        public void SetFormBody(string text)
        {
            Body = Encoding.UTF8.GetBytes(text ?? string.Empty);
            SetHeader("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");
        }

        public string DescribeForLog()
        {
            return Method + " " + Diagnostics.Redactor.Url(Url);
        }
    }

    /// <summary>One HTTP response, buffered or streaming.</summary>
    public sealed class HttpResponseData : IDisposable
    {
        private readonly List<KeyValuePair<string, string>> _headers =
            new List<KeyValuePair<string, string>>();

        public int StatusCode { get; set; }
        public byte[] Body { get; set; }
        public Stream BodyStream { get; set; }
        public string FinalUrl { get; set; }
        public string Location { get; set; }
        public long? ContentLength { get; set; }
        private string _contentRange;

        /// <summary>
        /// The <c>Content-Range</c> header.
        /// </summary>
        /// <remarks>
        /// Falls back to reading the header list when the property was not set
        /// explicitly. Range handling depends on this value for the total size, and a
        /// transport implementation that only populates the header list would
        /// otherwise silently look like a server that sent no <c>Content-Range</c> at
        /// all -- which degrades to a wrong file size rather than an error.
        /// </remarks>
        public string ContentRange
        {
            get { return _contentRange ?? GetHeader("Content-Range"); }
            set { _contentRange = value; }
        }

        private string _contentType;

        /// <summary>The <c>Content-Type</c> header, with the same fallback rule.</summary>
        public string ContentType
        {
            get { return _contentType ?? GetHeader("Content-Type"); }
            set { _contentType = value; }
        }

        public long ElapsedMs { get; set; }

        public IList<KeyValuePair<string, string>> Headers
        {
            get { return _headers; }
        }

        public bool IsSuccess
        {
            get { return StatusCode >= 200 && StatusCode < 300; }
        }

        public bool IsRedirect
        {
            get { return StatusCode == 301 || StatusCode == 302 || StatusCode == 303 || StatusCode == 307 || StatusCode == 308; }
        }

        public HttpResponseData AddHeader(string name, string value)
        {
            _headers.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            return this;
        }

        public string GetHeader(string name)
        {
            for (int index = 0; index < _headers.Count; index++)
            {
                if (string.Equals(_headers[index].Key, name, StringComparison.OrdinalIgnoreCase))
                    return _headers[index].Value;
            }
            return null;
        }

        public IList<string> GetHeaders(string name)
        {
            var result = new List<string>();
            for (int index = 0; index < _headers.Count; index++)
            {
                if (string.Equals(_headers[index].Key, name, StringComparison.OrdinalIgnoreCase))
                    result.Add(_headers[index].Value);
            }
            return result;
        }

        /// <summary>Every <c>Set-Cookie</c>, in the order the server sent them.</summary>
        public IList<string> SetCookies
        {
            get { return GetHeaders("Set-Cookie"); }
        }

        /// <summary>Body as UTF-8 text, or null when there is no buffered body.</summary>
        public string BodyText
        {
            get
            {
                if (Body == null) return null;
                if (Body.Length == 0) return string.Empty;
                return Encoding.UTF8.GetString(Body, 0, Body.Length);
            }
        }

        public int BodyLength
        {
            get { return Body == null ? 0 : Body.Length; }
        }

        public void Dispose()
        {
            if (BodyStream != null)
            {
                try
                {
                    BodyStream.Dispose();
                }
                catch (Exception)
                {
                    // Disposal failures must not mask the original result.
                }
                BodyStream = null;
            }
        }
    }

    /// <summary>
    /// The only network dependency of the Core layer.
    /// </summary>
    /// <remarks>
    /// Implemented once for the device (<c>Windows.Web.Http</c>) and once for
    /// desktop tests (a stub). Nothing above this interface knows which one is in
    /// use, which is what lets the whole client be exercised without a phone.
    /// </remarks>
    public interface IHttpTransport
    {
        System.Threading.Tasks.Task<HttpResponseData> SendAsync(
            HttpRequestSpec request,
            System.Threading.CancellationToken cancellationToken);
    }
}
