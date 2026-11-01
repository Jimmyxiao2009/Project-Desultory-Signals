using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Models
{
    /// <summary>Normalized author. Field names match the Python contract exactly.
    /// Never construct these from raw Douyin payloads -- that happens in Python.</summary>
    public sealed class Author
    {
        public string Id { get; set; }
        public string SecUid { get; set; }
        public string Nickname { get; set; }
        public string AvatarUrl { get; set; }
        public string UniqueId { get; set; }
        public string Signature { get; set; }

        public static Author FromJson(JObject source)
        {
            if (source == null) return new Author();
            return new Author
            {
                Id = JsonValue.GetString(source, "id"),
                SecUid = JsonValue.GetString(source, "secUid"),
                Nickname = JsonValue.GetString(source, "nickname"),
                AvatarUrl = JsonValue.GetUrl(source, "avatarUrl"),
                UniqueId = JsonValue.GetString(source, "uniqueId"),
                Signature = JsonValue.GetString(source, "signature")
            };
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Nickname)) return Nickname;
                if (!string.IsNullOrEmpty(UniqueId)) return "@" + UniqueId;
                return "未知用户";
            }
        }
    }

    /// <summary>One playable candidate for a video.</summary>
    public sealed class VideoSource
    {
        /// <summary>The URL to try first (https twin preferred by the selector).</summary>
        public string Url { get; set; }

        /// <summary>The URL exactly as the server gave it; also the dedupe key.</summary>
        public string OriginalUrl { get; set; }

        /// <summary>"h264" | "hevc" | "av1" | "unknown". See Python response_adapter.</summary>
        public string Codec { get; set; }

        /// <summary>Which payload field this came from; shown in diagnostics when a
        /// codec guess turns out wrong on the device.</summary>
        public string CodecEvidence { get; set; }

        public string Container { get; set; }
        public string Quality { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int? Bitrate { get; set; }
        public long? SizeBytes { get; set; }
        public string GearName { get; set; }

        public static VideoSource FromJson(JObject source)
        {
            if (source == null) return null;
            string url = JsonValue.GetUrl(source, "url");
            if (string.IsNullOrEmpty(url)) return null;
            return new VideoSource
            {
                Url = url,
                OriginalUrl = JsonValue.GetString(source, "originalUrl", url),
                Codec = JsonValue.GetString(source, "codec", "unknown"),
                CodecEvidence = JsonValue.GetString(source, "codecEvidence"),
                Container = JsonValue.GetString(source, "container", "mp4"),
                Quality = JsonValue.GetString(source, "quality", "unknown"),
                Width = JsonValue.GetInt(source, "width", 0),
                Height = JsonValue.GetInt(source, "height", 0),
                Bitrate = JsonValue.GetInt(source, "bitrate"),
                SizeBytes = JsonValue.GetLong(source, "sizeBytes"),
                GearName = JsonValue.GetString(source, "gearName")
            };
        }
    }

    public sealed class VideoInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int DurationMs { get; set; }
        public string CoverUrl { get; set; }
        public string DynamicCoverUrl { get; set; }
        public string Format { get; set; }
        public bool HasWatermark { get; set; }
        public IList<VideoSource> Sources { get; set; }

        public VideoInfo()
        {
            Sources = new List<VideoSource>();
        }

        public static VideoInfo FromJson(JObject source)
        {
            var info = new VideoInfo();
            if (source == null) return info;
            info.Width = JsonValue.GetInt(source, "width", 0);
            info.Height = JsonValue.GetInt(source, "height", 0);
            info.DurationMs = JsonValue.GetInt(source, "durationMs", 0);
            info.CoverUrl = JsonValue.GetUrl(source, "coverUrl");
            info.DynamicCoverUrl = JsonValue.GetUrl(source, "dynamicCoverUrl");
            info.Format = JsonValue.GetString(source, "format");
            info.HasWatermark = JsonValue.GetBool(source, "hasWatermark", false);
            foreach (JObject item in JsonValue.GetObjects(source, "sources"))
            {
                VideoSource parsed = VideoSource.FromJson(item);
                if (parsed != null) info.Sources.Add(parsed);
            }
            return info;
        }

        public bool HasAnySource
        {
            get { return Sources != null && Sources.Count > 0; }
        }
    }

    public sealed class Statistics
    {
        public int DiggCount { get; set; }
        public int CommentCount { get; set; }
        public int ShareCount { get; set; }
        public int CollectCount { get; set; }
        public int PlayCount { get; set; }

        public static Statistics FromJson(JObject source)
        {
            if (source == null) return new Statistics();
            return new Statistics
            {
                DiggCount = JsonValue.GetInt(source, "diggCount", 0),
                CommentCount = JsonValue.GetInt(source, "commentCount", 0),
                ShareCount = JsonValue.GetInt(source, "shareCount", 0),
                CollectCount = JsonValue.GetInt(source, "collectCount", 0),
                PlayCount = JsonValue.GetInt(source, "playCount", 0)
            };
        }
    }

    public sealed class ImageAsset
    {
        public string Url { get; set; }
        public IList<string> Urls { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class Aweme
    {
        public string Id { get; set; }
        public string Desc { get; set; }
        public long? CreateTime { get; set; }
        public Author Author { get; set; }
        public VideoInfo Video { get; set; }
        public Statistics Statistics { get; set; }
        public bool IsImage { get; set; }
        public IList<ImageAsset> Images { get; set; }
        public string ShareUrl { get; set; }
        public int DurationMs { get; set; }

        public static Aweme FromJson(JObject source)
        {
            if (source == null) return null;
            var aweme = new Aweme
            {
                Id = JsonValue.GetString(source, "id"),
                Desc = JsonValue.GetString(source, "desc", string.Empty),
                CreateTime = JsonValue.GetLong(source, "createTime"),
                Author = Author.FromJson(JsonValue.AsObject(source["author"])),
                Video = VideoInfo.FromJson(JsonValue.AsObject(source["video"])),
                Statistics = Statistics.FromJson(JsonValue.AsObject(source["statistics"])),
                IsImage = JsonValue.GetBool(source, "isImage", false),
                ShareUrl = JsonValue.GetUrl(source, "shareUrl"),
                DurationMs = JsonValue.GetInt(source, "durationMs", 0),
                Images = new List<ImageAsset>()
            };

            // An item without an id cannot be addressed, cached or deduplicated,
            // so it is rejected here rather than flowing into the feed as a ghost.
            if (string.IsNullOrEmpty(aweme.Id)) return null;

            foreach (JObject item in JsonValue.GetObjects(source, "images"))
            {
                var asset = new ImageAsset
                {
                    Url = JsonValue.GetUrl(item, "url"),
                    Urls = JsonValue.GetStrings(item, "urls"),
                    Width = JsonValue.GetInt(item, "width", 0),
                    Height = JsonValue.GetInt(item, "height", 0)
                };
                if (!string.IsNullOrEmpty(asset.Url)) aweme.Images.Add(asset);
            }

            return aweme;
        }

        /// <summary>Cover for the list, falling back to a still from the video.</summary>
        public string BestCoverUrl
        {
            get
            {
                if (Video != null && !string.IsNullOrEmpty(Video.CoverUrl)) return Video.CoverUrl;
                if (Images != null && Images.Count > 0) return Images[0].Url;
                return null;
            }
        }
    }

    public sealed class Comment
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public long? CreateTime { get; set; }
        public int DiggCount { get; set; }
        public int ReplyCount { get; set; }
        public Author Author { get; set; }
        public string ReplyToUsername { get; set; }
        public string ReplyToCommentId { get; set; }
        public int Level { get; set; }
        public int Status { get; set; }
        public bool IsAuthor { get; set; }
        public bool IsPinned { get; set; }

        public static Comment FromJson(JObject source)
        {
            if (source == null) return null;
            var comment = new Comment
            {
                Id = JsonValue.GetString(source, "id"),
                Text = JsonValue.GetString(source, "text", string.Empty),
                CreateTime = JsonValue.GetLong(source, "createTime"),
                DiggCount = JsonValue.GetInt(source, "diggCount", 0),
                ReplyCount = JsonValue.GetInt(source, "replyCount", 0),
                Author = Author.FromJson(JsonValue.AsObject(source["author"])),
                ReplyToUsername = JsonValue.GetString(source, "replyToUsername"),
                ReplyToCommentId = JsonValue.GetString(source, "replyToCommentId"),
                Level = JsonValue.GetInt(source, "level", 1),
                Status = JsonValue.GetInt(source, "status", 0),
                IsAuthor = JsonValue.GetBool(source, "isAuthor", false),
                IsPinned = JsonValue.GetBool(source, "isPinned", false)
            };
            if (string.IsNullOrEmpty(comment.Id)) return null;
            return comment;
        }

        public bool HasReplies
        {
            get { return ReplyCount > 0; }
        }
    }

    public sealed class FeedPage
    {
        public IList<Aweme> Items { get; set; }
        /// <summary>Opaque cursor to pass back verbatim. Never parse or increment it.</summary>
        public string Cursor { get; set; }
        public bool HasMore { get; set; }
        public string SourceKey { get; set; }
        /// <summary>Entries Python could not normalize. Surfaced so a rising count is
        /// visible in diagnostics instead of looking like an emptier feed.</summary>
        public int Skipped { get; set; }
        public string LogId { get; set; }

        public FeedPage()
        {
            Items = new List<Aweme>();
        }

        public static FeedPage FromJson(JObject source)
        {
            var page = new FeedPage();
            if (source == null) return page;
            page.Cursor = JsonValue.GetString(source, "cursor");
            page.HasMore = JsonValue.GetBool(source, "hasMore", false);
            page.SourceKey = JsonValue.GetString(source, "sourceKey");
            page.Skipped = JsonValue.GetInt(source, "skipped", 0);
            page.LogId = JsonValue.GetString(source, "logId");
            foreach (JObject item in JsonValue.GetObjects(source, "items"))
            {
                Aweme aweme = Aweme.FromJson(item);
                if (aweme != null) page.Items.Add(aweme);
            }
            return page;
        }
    }

    public sealed class CommentPage
    {
        public IList<Comment> Items { get; set; }
        public string Cursor { get; set; }
        public bool HasMore { get; set; }
        public int Total { get; set; }
        public int Skipped { get; set; }

        public CommentPage()
        {
            Items = new List<Comment>();
        }

        public static CommentPage FromJson(JObject source)
        {
            var page = new CommentPage();
            if (source == null) return page;
            page.Cursor = JsonValue.GetString(source, "cursor");
            page.HasMore = JsonValue.GetBool(source, "hasMore", false);
            page.Total = JsonValue.GetInt(source, "total", 0);
            page.Skipped = JsonValue.GetInt(source, "skipped", 0);
            foreach (JObject item in JsonValue.GetObjects(source, "items"))
            {
                Comment comment = Comment.FromJson(item);
                if (comment != null) page.Items.Add(comment);
            }
            return page;
        }
    }

    public sealed class UserProfile
    {
        public string Id { get; set; }
        public string SecUid { get; set; }
        public string Nickname { get; set; }
        public string AvatarUrl { get; set; }
        public string UniqueId { get; set; }
        public string Signature { get; set; }
        public int FollowingCount { get; set; }
        public int FollowerCount { get; set; }
        public int AwemeCount { get; set; }

        public static UserProfile FromJson(JObject source)
        {
            if (source == null) return null;
            return new UserProfile
            {
                Id = JsonValue.GetString(source, "id"),
                SecUid = JsonValue.GetString(source, "secUid"),
                Nickname = JsonValue.GetString(source, "nickname"),
                AvatarUrl = JsonValue.GetUrl(source, "avatarUrl"),
                UniqueId = JsonValue.GetString(source, "uniqueId"),
                Signature = JsonValue.GetString(source, "signature"),
                FollowingCount = JsonValue.GetInt(source, "followingCount", 0),
                FollowerCount = JsonValue.GetInt(source, "followerCount", 0),
                AwemeCount = JsonValue.GetInt(source, "awemeCount", 0)
            };
        }
    }

    public sealed class SearchPage
    {
        public IList<Aweme> Videos { get; private set; }
        public IList<UserProfile> Users { get; private set; }
        public string Cursor { get; private set; }
        public string SearchId { get; private set; }
        public bool HasMore { get; private set; }
        public int Skipped { get; private set; }
        public static SearchPage FromJson(JObject source)
        {
            var result = new SearchPage
            {
                Videos = new List<Aweme>(), Users = new List<UserProfile>(),
                Cursor = JsonValue.GetString(source, "cursor"), SearchId = JsonValue.GetString(source, "searchId"),
                HasMore = JsonValue.GetBool(source, "hasMore", false), Skipped = JsonValue.GetInt(source, "skipped", 0)
            };
            foreach (JObject raw in JsonValue.GetObjects(source, "videos"))
            {
                Aweme item = Aweme.FromJson(raw);
                if (item != null) result.Videos.Add(item);
            }
            foreach (JObject raw in JsonValue.GetObjects(source, "users"))
            {
                UserProfile user = UserProfile.FromJson(raw);
                if (user != null) result.Users.Add(user);
            }
            return result;
        }
    }

    public sealed class ShareParseResult
    {
        /// <summary>"aweme_id" | "redirect" | "unsupported"</summary>
        public string Kind { get; set; }
        public string AwemeId { get; set; }
        public string NextUrl { get; set; }
        public string Source { get; set; }
        public string Reason { get; set; }
        public string Input { get; set; }

        public static ShareParseResult FromJson(JObject source)
        {
            if (source == null) return null;
            return new ShareParseResult
            {
                Kind = JsonValue.GetString(source, "kind"),
                AwemeId = JsonValue.GetString(source, "awemeId"),
                NextUrl = JsonValue.GetString(source, "nextUrl"),
                Source = JsonValue.GetString(source, "source"),
                Reason = JsonValue.GetString(source, "reason"),
                Input = JsonValue.GetString(source, "input")
            };
        }

        public bool NeedsRedirect
        {
            get { return Kind == "redirect" && !string.IsNullOrEmpty(NextUrl); }
        }
    }

    /// <summary>Guest-or-logged-in assessment. Produced by Python from the jar, so a
    /// stale C# flag can never disagree with the cookies actually held.</summary>
    public sealed class SessionAssessment
    {
        public string State { get; set; }
        public IList<string> SessionMarkers { get; set; }
        public int CookieCount { get; set; }

        public bool IsLoggedIn
        {
            get { return State == "logged_in"; }
        }

        public static SessionAssessment FromJson(JObject source)
        {
            var assessment = new SessionAssessment { SessionMarkers = new List<string>() };
            if (source == null) return assessment;
            assessment.State = JsonValue.GetString(source, "state", "guest");
            assessment.CookieCount = JsonValue.GetInt(source, "cookieCount", 0);
            foreach (string name in JsonValue.GetStrings(source, "sessionMarkers"))
                assessment.SessionMarkers.Add(name);
            return assessment;
        }
    }

    /// <summary>Login state machine states, mirroring douyin/login.py.</summary>
    public static class LoginStatus
    {
        public const string Created = "Created";
        public const string WaitingForScan = "WaitingForScan";
        public const string Scanned = "Scanned";
        public const string WaitingForConfirmation = "WaitingForConfirmation";
        public const string Confirmed = "Confirmed";
        public const string Expired = "Expired";
        public const string Failed = "Failed";

        public static bool IsTerminal(string status)
        {
            return status == Confirmed || status == Expired || status == Failed;
        }
    }

    /// <summary>
    /// Read-only view of the QR login session. The machine's transition rules are
    /// enforced in Python; this type only carries the state to the caller.
    /// </summary>
    public sealed class LoginSessionModel
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string QrUrl { get; set; }
        public string QrImageBase64 { get; set; }
        public string StatusMessage { get; set; }
        public string ErrorKind { get; set; }
        public long ExpiresAtMs { get; set; }
        public int PollIntervalMs { get; set; }
        public string Token { get; set; }
        public JObject ResponseDiagnostic { get; set; }

        public bool IsTerminal
        {
            get { return LoginStatus.IsTerminal(Status); }
        }

        public static LoginSessionModel FromJson(JObject source)
        {
            if (source == null) return null;
            return new LoginSessionModel
            {
                Id = JsonValue.GetString(source, "id"),
                Status = JsonValue.GetString(source, "status", LoginStatus.Created),
                QrUrl = JsonValue.GetString(source, "qrUrl"),
                QrImageBase64 = JsonValue.GetString(source, "qrImageBase64"),
                StatusMessage = JsonValue.GetString(source, "statusMessage"),
                ErrorKind = JsonValue.GetString(source, "errorKind"),
                ExpiresAtMs = JsonValue.GetLong(source, "expiresAtMs") ?? 0,
                PollIntervalMs = JsonValue.GetInt(source, "pollIntervalMs", 2000),
                Token = JsonValue.GetString(source, "token")
            };
        }
    }
}
