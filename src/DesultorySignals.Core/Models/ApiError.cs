using System;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Models
{
    /// <summary>
    /// The error taxonomy. These strings cross the bridge from
    /// <c>python/douyin/errors.py</c> and must stay in sync with it.
    /// </summary>
    /// <remarks>
    /// The point of enumerating them is that each one has a *different* recovery.
    /// A retry loop that treats "403" as one condition will hammer the API on a
    /// signature bug and give up on a transient network blip, which is exactly
    /// backwards.
    /// </remarks>
    public static class ErrorKinds
    {
        public const string Network = "network";
        public const string HttpError = "http_error";
        public const string EmptyBody = "empty_body";
        public const string SignatureInvalid = "signature_invalid";
        public const string RiskControl = "risk_control";
        public const string SessionExpired = "session_expired";
        public const string LoginRequired = "login_required";
        public const string SchemaChanged = "schema_changed";
        public const string NotFound = "not_found";
        public const string RateLimited = "rate_limited";
        public const string UnsupportedCodec = "unsupported_codec";
        public const string SignerUnavailable = "signer_unavailable";
        public const string InvalidInput = "invalid_input";
        public const string Internal = "internal";

        /// <summary>A kind we do not recognise, i.e. the Python side moved ahead of
        /// this build. Treated as non-retryable and surfaced, never ignored.</summary>
        public const string Unknown = "unknown";

        /// <summary>Human-facing Chinese text for the debug UI.</summary>
        public static string Describe(string kind)
        {
            switch (kind)
            {
                case Network: return "网络不可用或请求超时";
                case HttpError: return "服务器返回错误状态";
                case EmptyBody: return "服务器返回空响应（通常意味着请求被静默丢弃）";
                case SignatureInvalid: return "请求签名被拒绝";
                case RiskControl: return "触发风控，需要人工处理";
                case LoginRequired: return "此功能需要登录";
                case SessionExpired: return "登录状态已失效，需要重新登录";
                case SchemaChanged: return "接口结构变化，解析层需要更新";
                case NotFound: return "内容不存在或已被删除";
                case RateLimited: return "被限流，需要退避";
                case UnsupportedCodec: return "没有设备可解码的视频源";
                case SignerUnavailable: return "签名实现未接入本次构建";
                case InvalidInput: return "输入不合法";
                case Internal: return "客户端内部错误";
                default: return "未知错误（" + kind + "）";
            }
        }
    }

    /// <summary>A classified failure. Immutable.</summary>
    public sealed class ApiError
    {
        public string Kind { get; private set; }
        public string Message { get; private set; }
        public int? HttpStatus { get; private set; }
        public bool Retryable { get; private set; }
        public JObject Detail { get; private set; }

        public ApiError(string kind, string message, int? httpStatus, bool retryable, JObject detail)
        {
            Kind = string.IsNullOrEmpty(kind) ? ErrorKinds.Unknown : kind;
            Message = message ?? string.Empty;
            HttpStatus = httpStatus;
            Retryable = retryable;
            Detail = detail;
        }

        public bool IsSessionProblem
        {
            get { return Kind == ErrorKinds.SessionExpired || Kind == ErrorKinds.LoginRequired; }
        }

        public bool IsSignatureProblem
        {
            get { return Kind == ErrorKinds.SignatureInvalid || Kind == ErrorKinds.SignerUnavailable; }
        }

        public static ApiError FromJson(JObject source)
        {
            if (source == null)
                return new ApiError(ErrorKinds.Unknown, "no error object was provided", null, false, null);
            return new ApiError(
                JsonValue.GetString(source, "kind", ErrorKinds.Unknown),
                JsonValue.GetString(source, "message", string.Empty),
                JsonValue.GetInt(source, "httpStatus"),
                JsonValue.GetBool(source, "retryable", false),
                JsonValue.AsObject(source["detail"]));
        }

        public static ApiError Local(string kind, string message, bool retryable)
        {
            return new ApiError(kind, message, null, retryable, null);
        }

        public string Describe()
        {
            string text = ErrorKinds.Describe(Kind) + "： " + Message;
            if (HttpStatus.HasValue) text += " (HTTP " + HttpStatus.Value + ")";
            return text;
        }

        public override string ToString()
        {
            return Kind + ": " + Message;
        }
    }

    /// <summary>Thrown by the Douyin layer when an operation fails in a classified way.</summary>
    public class DouyinApiException : Exception
    {
        public ApiError Error { get; private set; }

        public DouyinApiException(ApiError error)
            : base(error == null ? "unknown error" : error.ToString())
        {
            Error = error ?? ApiError.Local(ErrorKinds.Unknown, "unknown error", false);
        }

        public string Kind
        {
            get { return Error.Kind; }
        }

        public bool Retryable
        {
            get { return Error.Retryable; }
        }

        public static DouyinApiException Network(string message, Exception inner)
        {
            return new DouyinApiException(ApiError.Local(ErrorKinds.Network, message, true));
        }

        public static DouyinApiException Cancelled()
        {
            return new DouyinApiException(ApiError.Local(ErrorKinds.Internal, "cancelled", false));
        }
    }
}
