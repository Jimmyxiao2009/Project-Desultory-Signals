using System;
using System.Collections.Generic;
using DesultorySignals.Core.Models;

namespace DesultorySignals.Core.Media
{
    /// <summary>The chosen source plus why, and what is uncertain about it.</summary>
    public sealed class VideoSourceSelection
    {
        public VideoSource Selected { get; set; }
        public IList<VideoSource> Ranked { get; set; }
        public string Reason { get; set; }

        /// <summary>
        /// True when the label says "playable" but the payload never actually told us
        /// the codec. The playback layer must treat a failure on such a source as
        /// "this codec is wrong", not as a network problem, and may then try the
        /// next candidate.
        /// </summary>
        public bool NeedsDecodeProbe { get; set; }

        /// <summary>Set when nothing playable exists. Kind is
        /// <see cref="ErrorKinds.UnsupportedCodec"/>.</summary>
        public ApiError Failure { get; set; }

        public bool IsSupported
        {
            get { return Selected != null; }
        }

        public VideoSourceSelection()
        {
            Ranked = new List<VideoSource>();
        }
    }

    /// <summary>
    /// Picks a playable source for a Lumia-class device.
    /// </summary>
    /// <remarks>
    /// The target decoder is the Windows 10 Mobile media pipeline on an ARM32
    /// Snapdragon: H.264/AVC in MP4 with AAC is safe, HEVC is present on some
    /// devices but not reliably, and ByteVC1/AV1 is not there at all. So the policy
    /// is not "highest quality wins" -- it is "a codec we know decodes wins", and
    /// quality only breaks ties inside that set.
    ///
    /// The brief's stated order is H.264 720p, then 540p, then 1080p, then other
    /// H.264. That is expressed below as a score, with 720p deliberately above
    /// 1080p: on a 720x1280 screen a 1080p stream costs three times the bandwidth
    /// and buys nothing the panel can show.
    ///
    /// The one honest weakness: <c>video.play_addr</c> carries no codec field, so a
    /// source can be labelled <c>unknown</c>. Those are ranked below every known
    /// H.264 candidate but are still returned, flagged
    /// <see cref="VideoSourceSelection.NeedsDecodeProbe"/>, because refusing them
    /// outright would break playback for any payload that omits the explicit H.264
    /// field. A decode failure on such a source is attributed to the codec, and the
    /// caller moves to the next candidate.
    /// </remarks>
    public static class VideoSourceSelector
    {
        private const int CodecKnownH264 = 1000;
        private const int CodecUnknown = 300;
        private const int CodecHevc = 0;
        private const int CodecAv1 = 0;

        private const int ContainerMp4 = 100;
        private const int SchemeHttps = 40;

        public static VideoSourceSelection Select(IList<VideoSource> sources)
        {
            return Select(sources, false);
        }

        /// <param name="allowHevc">Only for a device that has been proven to decode
        /// HEVC. Never enabled by default; see the class remarks.</param>
        public static VideoSourceSelection Select(IList<VideoSource> sources, bool allowHevc)
        {
            var result = new VideoSourceSelection();

            if (sources == null || sources.Count == 0)
            {
                result.Failure = ApiError.Local(
                    ErrorKinds.UnsupportedCodec,
                    "the video carries no playable source at all",
                    false);
                return result;
            }

            var scored = new List<ScoredSource>();
            for (int index = 0; index < sources.Count; index++)
            {
                VideoSource source = sources[index];
                if (source == null || string.IsNullOrEmpty(source.Url)) continue;

                int score = Score(source, allowHevc);
                if (score < 0) continue; // codec we refuse outright
                scored.Add(new ScoredSource(source, score));
            }

            if (scored.Count == 0)
            {
                result.Failure = ApiError.Local(
                    ErrorKinds.UnsupportedCodec,
                    "every source uses a codec this device cannot decode "
                        + "(HEVC / ByteVC1 / AV1 only)",
                    false);
                return result;
            }

            scored.Sort(Compare);

            for (int index = 0; index < scored.Count; index++)
            {
                result.Ranked.Add(scored[index].Source);
            }

            VideoSource best = scored[0].Source;
            result.Selected = best;
            result.NeedsDecodeProbe = string.Equals(best.Codec, "unknown", StringComparison.OrdinalIgnoreCase);
            result.Reason = string.Format(
                "{0} {1} {2} via {3}{4}",
                best.Codec,
                best.Quality,
                best.Container,
                best.CodecEvidence,
                result.NeedsDecodeProbe ? " (codec unverified, will probe by decoding)" : string.Empty);
            return result;
        }

        private static int Score(VideoSource source, bool allowHevc)
        {
            string codec = (source.Codec ?? "unknown").ToLowerInvariant();
            int score;

            switch (codec)
            {
                case "h264":
                    score = CodecKnownH264;
                    break;
                case "unknown":
                    // An unknown codec is only acceptable when the container is one we
                    // can demux and there is no evidence it is HEVC.
                    if (!IsMp4(source.Container)) return -1;
                    score = CodecUnknown;
                    break;
                case "hevc":
                    if (!allowHevc) return -1;
                    score = CodecKnownH264 - 200;
                    break;
                case "av1":
                    return -1;
                default:
                    if (!IsMp4(source.Container)) return -1;
                    score = CodecUnknown;
                    break;
            }

            if (IsMp4(source.Container)) score += ContainerMp4;

            if (source.Url != null && source.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                score += SchemeHttps;

            score += QualityScore(source);
            return score;
        }

        private static bool IsMp4(string container)
        {
            if (string.IsNullOrEmpty(container)) return true; // unlabelled defaults to mp4 upstream
            return container.Equals("mp4", StringComparison.OrdinalIgnoreCase);
        }

        private static int QualityScore(VideoSource source)
        {
            string quality = source.Quality ?? "unknown";
            switch (quality)
            {
                // The stated preference: 720p, then 540p, then 1080p, then the rest.
                case "720p": return 70;
                case "540p": return 65;
                case "1080p": return 60;
                case "480p": return 50;
                case "360p": return 40;
                case "1440p": return 30;
                case "2160p": return 25;
                case "240p": return 20;
                default: return 35;
            }
        }

        private static int Compare(ScoredSource left, ScoredSource right)
        {
            if (left.Score != right.Score) return right.Score.CompareTo(left.Score);

            // The payload's first-party play endpoint works when prime CDN URLs
            // are refused. Prefer it only among the same codec/quality score.
            bool leftPlay = IsFirstPartyPlay(left.Source.Url);
            bool rightPlay = IsFirstPartyPlay(right.Source.Url);
            if (leftPlay != rightPlay) return leftPlay ? -1 : 1;

            // Tie-break 1: a known file size, smaller first. On a phone the
            // difference between a 12 MB and a 45 MB stream at the same score is
            // buffering, and buffering is the thing the user notices.
            long leftSize = left.Source.SizeBytes ?? long.MaxValue;
            long rightSize = right.Source.SizeBytes ?? long.MaxValue;
            if (leftSize != rightSize) return leftSize.CompareTo(rightSize);

            // Tie-break 2: bitrate, lower first, same reasoning.
            int leftBitrate = left.Source.Bitrate ?? int.MaxValue;
            int rightBitrate = right.Source.Bitrate ?? int.MaxValue;
            if (leftBitrate != rightBitrate) return leftBitrate.CompareTo(rightBitrate);

            // Tie-break 3: stable, so two equal candidates do not swap order between
            // runs (which would make caching and diagnostics non-reproducible).
            return string.CompareOrdinal(left.Source.Url, right.Source.Url);
        }

        private static bool IsFirstPartyPlay(string url)
        {
            Uri parsed;
            return Uri.TryCreate(url, UriKind.Absolute, out parsed)
                && parsed.Scheme == "https" && parsed.Host.Equals("www.douyin.com", StringComparison.OrdinalIgnoreCase)
                && parsed.AbsolutePath.StartsWith("/aweme/v1/play/", StringComparison.Ordinal);
        }

        private struct ScoredSource
        {
            public readonly VideoSource Source;
            public readonly int Score;

            public ScoredSource(VideoSource source, int score)
            {
                Source = source;
                Score = score;
            }
        }
    }
}
