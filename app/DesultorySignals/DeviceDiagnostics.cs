using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.ApplicationModel;
using Windows.Storage;

namespace DesultorySignals
{
    // Fixed read/playback checks, opt-in through LocalState/device-check.txt.
    // Never imports credentials or publishes account actions.
    internal sealed class DeviceDiagnostics
    {
        private readonly object _fileGate = new object();
        private readonly Timer _timer;
        private int _dirty = 1;
        private readonly JObject _report = new JObject();
        private readonly JArray _steps = new JArray();
        private readonly string _root = ApplicationData.Current.LocalFolder.Path;
        public DeviceDiagnostics()
        {
            App.Log.EntryWritten += delegate { Interlocked.Exchange(ref _dirty, 1); };
            _timer = new Timer(delegate { FlushLog(); }, null, 3000, 3000);
        }
        public void FlushLog()
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            lock (_fileGate)
            {
                try { File.WriteAllText(Path.Combine(_root, "runtime-log.txt"), App.Log.Dump()); }
                catch { Interlocked.Exchange(ref _dirty, 1); }
            }
        }
        private void Save()
        {
            _report["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("o");
            File.WriteAllText(Path.Combine(_root, "device-check-report.json"), _report.ToString(Formatting.Indented));
            FlushLog();
        }
        private async Task Step(string name, Func<CancellationToken, Task<JObject>> check, CancellationToken token)
        {
            var step = new JObject { { "name", name }, { "status", "running" } };
            _steps.Add(step);
            Save();
            App.Log.Info(DiagCategory.Lifecycle, "device check: " + name);
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(name == "download-playback" ? 180000 : 60000);
                    step["result"] = await check(deadline.Token);
                }
                step["status"] = "passed";
            }
            catch (Exception ex)
            {
                step["status"] = "failed";
                step["error"] = App.Describe(ex);
                var api = ex as DouyinApiException;
                if (api != null)
                {
                    step["kind"] = api.Kind;
                    step["httpStatus"] = api.Error.HttpStatus;
                    if (api.Error.Detail != null)
                    {
                        step["gate"] = JsonValue.GetString(api.Error.Detail, "gate");
                        step["missingCredential"] = JsonValue.GetString(api.Error.Detail, "missingCredential");
                    }
                }
                App.Log.Error(DiagCategory.Lifecycle, "device check " + name + ": " + App.Describe(ex));
            }
            Save();
        }
        public async Task RunIfRequestedAsync(CancellationToken token)
        {
            string flag = Path.Combine(_root, "device-check.txt");
            if (!File.Exists(flag)) return;
            File.Delete(flag); // One run per request; ordinary launches do not play media.
            _report["package"] = Package.Current.Id.FullName;
            _report["startedAtUtc"] = DateTimeOffset.UtcNow.ToString("o");
            _report["steps"] = _steps;
            _report["complete"] = false;
            Aweme video = null;
            Comment parentComment = null;
            await Step("session", async ct =>
            {
                var state = await App.Services.Api.CheckSessionAsync(ct);
                return new JObject { { "state", state.State }, { "cookieCount", state.CookieCount } };
            }, token);
            await Step("feed", async ct =>
            {
                if (App.Services.Feed.Count == 0) await App.Services.Feed.LoadInitialAsync(10, ct);
                foreach (Aweme item in App.Services.Feed.Items)
                    if (!item.IsImage && item.Video != null && item.Video.HasAnySource
                        && (video == null || item.DurationMs < video.DurationMs)) video = item;
                if (App.Services.Feed.Count == 0) throw new InvalidOperationException("Feed returned no items");
                return new JObject { { "count", App.Services.Feed.Count }, { "videoId", video == null ? null : video.Id } };
            }, token);
            if (video != null)
            {
                await Step("detail", async ct =>
                {
                    var detail = await App.Services.Video.GetDetailAsync(video.Id, true, ct);
                    if (detail == null) throw new InvalidOperationException("detail returned no item");
                    return new JObject { { "id", detail.Id }, { "hasSources", detail.Video != null && detail.Video.HasAnySource } };
                }, token);
                await Step("share", async ct =>
                {
                    var parsed = await App.Services.Api.ResolveShareAsync("https://www.douyin.com/video/" + video.Id, ct);
                    if (parsed.AwemeId != video.Id) throw new InvalidOperationException("share id mismatch");
                    return new JObject { { "id", parsed.AwemeId } };
                }, token);
                await Step("metadata-cache", async ct =>
                {
                    await App.Services.Cache.PutAwemeAsync(video, ct);
                    var cached = await App.Services.Cache.GetAwemeAsync(video.Id, ct);
                    if (cached == null || cached.Id != video.Id) throw new InvalidOperationException("cache roundtrip failed");
                    return new JObject { { "id", cached.Id } };
                }, token);
                await Step("comments", async ct =>
                {
                    var comments = await App.Services.Comments.LoadFirstPageAsync(video.Id, 10, ct);
                    foreach (Comment comment in comments) if (comment.HasReplies) { parentComment = comment; break; }
                    var thread = App.Services.Comments.Thread(video.Id);
                    return new JObject { { "count", comments.Count }, { "total", thread.Total }, { "hasMore", thread.HasMore } };
                }, token);
                await Step("comments-next", async ct =>
                {
                    var added = await App.Services.Comments.LoadNextPageAsync(video.Id, 10, ct);
                    return new JObject { { "added", added.Count }, { "total", App.Services.Comments.Thread(video.Id).Count } };
                }, token);
                if (parentComment != null)
                    await Step("comment-replies", async ct =>
                    {
                        var replies = await App.Services.Comments.LoadRepliesAsync(video.Id, parentComment.Id, "0", 10, ct);
                        return new JObject { { "count", replies.Items.Count }, { "total", replies.Total } };
                    }, token);
                await Step("stream-playback", async ct =>
                {
                    try
                    {
                        await App.Services.Playback.StopAsync();
                        await App.Services.Playback.PlayStreamingAsync(video, ct);
                        var playing = await WaitForPlaybackAsync(ct);
                        double target = Math.Min((double)playing["durationMs"] / 2, 30000);
                        await App.Media.SeekAsync(TimeSpan.FromMilliseconds(target));
                        var sought = await WaitForPlaybackAsync(ct, target + 500);
                        await App.Media.PauseAsync();
                        JObject paused = null;
                        for (int wait = 0; wait < 20; wait++)
                        {
                            await Task.Delay(100, ct);
                            paused = await App.Media.DescribeAsync();
                            if ((string)paused["state"] == "Paused") break;
                        }
                        if ((string)paused["state"] != "Paused") throw new InvalidOperationException("pause did not take effect");
                        await App.Media.ResumeAsync();
                        var resumed = await WaitForPlaybackAsync(ct, (double)paused["positionMs"] + 500);
                        return new JObject { { "playing", playing }, { "afterSeek", sought }, { "paused", paused }, { "resumed", resumed } };
                    }
                    finally { await App.Services.Playback.StopAsync(); }
                }, token);
                await Step("download-playback", async ct =>
                {
                    try
                    {
                        await App.Services.Playback.PlayDownloadedAsync(video, App.Files.RootPath, ct);
                        var playing = await WaitForPlaybackAsync(ct);
                        double target = Math.Min((double)playing["durationMs"] / 2, 30000);
                        await App.Media.SeekAsync(TimeSpan.FromMilliseconds(target));
                        var sought = await WaitForPlaybackAsync(ct, target + 500);
                        await App.Media.PauseAsync();
                        JObject paused = null;
                        for (int wait = 0; wait < 20; wait++)
                        {
                            await Task.Delay(100, ct);
                            paused = await App.Media.DescribeAsync();
                            if ((string)paused["state"] == "Paused") break;
                        }
                        if ((string)paused["state"] != "Paused") throw new InvalidOperationException("pause did not take effect");
                        await App.Media.ResumeAsync();
                        var resumed = await WaitForPlaybackAsync(ct, (double)paused["positionMs"] + 500);
                        return new JObject { { "playing", playing }, { "afterSeek", sought }, { "paused", paused }, { "resumed", resumed } };
                    }
                    finally { await App.Services.Playback.StopAsync(); }
                }, token);
            }
            await Step("feed-next", async ct =>
            {
                var next = await App.Services.Feed.LoadMoreAsync(6, ct);
                return new JObject { { "added", next.Added.Count }, { "total", next.TotalItems }, { "hasMore", next.HasMore } };
            }, token);
            await Step("feed-refresh", async ct =>
            {
                var refresh = await App.Services.Feed.RefreshAsync(6, ct);
                if (refresh.TotalItems == 0) throw new InvalidOperationException("refreshed Feed is empty");
                return new JObject { { "total", refresh.TotalItems } };
            }, token);
            JObject identity = await App.Services.Session.DescribeRedactedAsync(token);
            _report["identity"] = new JObject
            {
                { "ttwid", JsonValue.GetString(identity, "ttwid") },
                { "uifid", JsonValue.GetString(identity, "uifid") },
                { "cookieCount", JsonValue.GetInt(identity, "cookieCount", 0) }
            };
            _report["complete"] = true;
            int failures = 0;
            foreach (JObject step in _steps) if ((string)step["status"] != "passed") failures++;
            _report["failures"] = failures;
            App.Log.Info(DiagCategory.Lifecycle, "device checks complete: failures=" + failures);
            Save();
        }
        private async Task<JObject> WaitForPlaybackAsync(CancellationToken token, double minimumPositionMs = 1500)
        {
            JObject state = null;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000, token);
                state = await App.Media.DescribeAsync();
                if ((string)state["error"] != null) throw new InvalidOperationException((string)state["error"]);
                if ((double?)state["positionMs"] > minimumPositionMs && (string)state["state"] == "Playing") return state;
            }
            throw new InvalidOperationException("MediaElement never advanced: " + state);
        }
    }
}
