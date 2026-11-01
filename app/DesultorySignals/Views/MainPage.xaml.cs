using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Services;
using DesultorySignals.Platform;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace DesultorySignals.Views
{
    /// <summary>
    /// The debug surface: every UI event calls <see cref="ServiceHost"/> and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// There is no business logic in this file on purpose. Each handler is one line of
    /// "call a service method, render a result", and every one of them goes through
    /// <see cref="Run"/>, which catches <c>OperationCanceledException</c> and every
    /// other exception and turns it into classified text on the status bar. That is the
    /// only reason the shell can be left running on a device: an exception must never
    /// reach the dispatcher, because that is an app crash rather than a failed
    /// operation.
    ///
    /// Page lifetime is explicit. <see cref="_pageCts"/> is cancelled in
    /// <c>OnNavigatedFrom</c> so an in-flight request cannot write into a page that is
    /// gone, and <see cref="ServiceHost.ReleasePageResourcesAsync"/> is awaited at the
    /// same moment so the media pipeline and the loaded-comment list are released --
    /// the acceptance criterion is "continuous scrolling and playback do not grow
    /// memory", and this is the page's half of it.
    /// </remarks>
    public sealed partial class MainPage : Page
    {
        private readonly ObservableCollection<FeedRow> _feedRows = new ObservableCollection<FeedRow>();
        private readonly ObservableCollection<CommentRow> _commentRows = new ObservableCollection<CommentRow>();

        private readonly ObservableCollection<FeedRow> _searchVideos = new ObservableCollection<FeedRow>();
        private readonly ObservableCollection<UserRow> _searchUsers = new ObservableCollection<UserRow>();
        private readonly ObservableCollection<FeedRow> _profileVideos = new ObservableCollection<FeedRow>();
        private string _searchQuery, _searchCursor, _searchId, _profileUid, _profileCursor;
        private bool _searchIsUsers, _searchHasMore, _profileHasMore;

        private CancellationTokenSource _pageCts = new CancellationTokenSource();
        private bool _busy;
        private CancellationTokenSource _operationCts;
        private Aweme _current;
        private ResolvedVideoSource _resolved;
        private bool _codecUnverified;
        private string _retriedAwemeId;
        private LoginSessionModel _loginSession;

        public MainPage()
        {
            InitializeComponent();
            FeedList.ItemsSource = _feedRows;
            CommentList.ItemsSource = _commentRows;
            SearchVideos.ItemsSource = _searchVideos;
            SearchUsers.ItemsSource = _searchUsers;
            ProfileVideos.ItemsSource = _profileVideos;
        }

        // ------------------------------------------------------------------
        // page lifetime
        // ------------------------------------------------------------------

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_pageCts.IsCancellationRequested)
            {
                _pageCts.Dispose();
                _pageCts = new CancellationTokenSource();
            }

            App.Media.Attach(MediaSurface);
            App.Media.PlaybackError += OnPlaybackError;
            App.BootstrapCompleted += OnBootstrapCompleted;
            App.Services.Playback.StateChanged += OnPlaybackStateChanged;
            App.Services.Feed.Updated += OnFeedUpdated;

            BootstrapReport report = App.Bootstrap;
            if (report == null)
            {
                StatusText.Text = "正在初始化运行时...";
            }
            else
            {
                RenderBootstrap(report);
            }

            RenderSession();
            RenderFeed(null);
            if (report != null) OpenLoginIfRequested();
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            App.Media.PlaybackError -= OnPlaybackError;
            App.BootstrapCompleted -= OnBootstrapCompleted;
            App.Services.Playback.StateChanged -= OnPlaybackStateChanged;
            App.Services.Feed.Updated -= OnFeedUpdated;

            _pageCts.Cancel();

            try
            {
                // Stop the decoder and drop the per-page caches before the page goes.
                var release = App.Services.ReleasePageResourcesAsync();
                App.Media.Attach(null);
                await release;
            }
            catch (Exception exception)
            {
                App.Log.Warn(DiagCategory.Lifecycle,
                    "releasing page resources failed: " + App.Describe(exception));
            }

        }

        private void OnBootstrapCompleted(BootstrapReport report)
        {
            OnUi(delegate
            {
                RenderBootstrap(report);
                RenderSession();
                RenderFeed(null);
                OpenLoginIfRequested();
            });
        }

        /// <summary>
        /// Run a view update on the UI thread.
        /// </summary>
        /// <remarks>
        /// Not every callback that reaches this page originates on the UI thread:
        /// <c>PlaybackService.StateChanged</c> is raised from wherever the awaited
        /// continuation happened to land, and the Core layer deliberately does not use
        /// <c>ConfigureAwait(true)</c> on the hot path. Touching a XAML element from
        /// there is a marshalling violation, so every notification goes through here.
        /// </remarks>
        private void OnUi(Action action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    App.Log.Warn(DiagCategory.Lifecycle, "a view update failed: " + exception.Message);
                }
                return;
            }

            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    App.Log.Warn(DiagCategory.Lifecycle, "a view update failed: " + exception.Message);
                }
            });
            _ = ignored;
        }

        private void RenderBootstrap(BootstrapReport report)
        {
            StatusText.Text = report.Summary();
        }

        private void RenderSession()
        {
            SessionAssessment assessment = App.Services.Session == null
                ? null
                : App.Services.Session.Assessment;

            if (assessment == null)
            {
                SessionText.Text = "会话：未知";
                return;
            }

            SessionText.Text = string.Format(
                "会话：{0}（cookie {1}，标记 {2}）",
                assessment.State,
                assessment.CookieCount,
                assessment.SessionMarkers == null || assessment.SessionMarkers.Count == 0
                    ? "无"
                    : string.Join(",", ToArray(assessment.SessionMarkers)));
        }

        private void OnFeedUpdated(FeedUpdate update)
        {
            OnUi(delegate { RenderFeed(update); });
        }

        private void OnPlaybackStateChanged(PlaybackState state)
        {
            OnUi(delegate { ActionText.Text = "播放状态：" + state; });
        }

        // ------------------------------------------------------------------
        // the single error boundary every handler goes through
        // ------------------------------------------------------------------

        private async void Run(string label, Func<CancellationToken, Task> work)
        {
            if (App.Bootstrap == null || App.Bootstrap.CompletedAtMs == 0)
            {
                Say("正在初始化，请稍后再试");
                return;
            }
            if (_busy) { Say("上一项操作仍在进行，请稍候"); return; }
            _busy = true;
            _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
            FeedList.IsEnabled = false;
            ActionText.Text = label + " ...";
            App.Log.Info(DiagCategory.Lifecycle, "UI action: " + label);
            try
            {
                await work(_operationCts.Token);
            }
            catch (OperationCanceledException)
            {
                ActionText.Text = label + " 已取消";
            }
            catch (Exception exception)
            {
                string text = label + " 失败：" + App.Describe(exception);
                ActionText.Text = text;
                App.Log.Error(DiagCategory.Lifecycle, text);
            }
            finally
            {
                _busy = false;
                FeedList.IsEnabled = true;
                _operationCts.Dispose();
                _operationCts = null;
            }
        }

        private void Say(string text)
        {
            ActionText.Text = text;
        }

        // ------------------------------------------------------------------
        // open one aweme
        // ------------------------------------------------------------------

        private void OnOpenClick(object sender, RoutedEventArgs e)
        {
            string input = (IdBox.Text ?? string.Empty).Trim();
            if (input.Length == 0)
            {
                Say("请输入 aweme_id 或分享链接");
                return;
            }

            Run("打开", async token =>
            {
                string awemeId = input;
                if (!IsDigits(input))
                {
                    ShareParseResult parsed = await App.Services.Api.ResolveShareAsync(input, token);
                    awemeId = parsed == null ? null : parsed.AwemeId;
                    if (string.IsNullOrEmpty(awemeId))
                    {
                        string kind = parsed == null ? "(null)" : parsed.Kind;
                        string reason = parsed == null ? string.Empty : parsed.Reason;
                        throw new DouyinApiException(ApiError.Local(
                            ErrorKinds.NotFound,
                            "分享链接没有解析出 aweme_id（kind=" + kind + " " + reason + "）",
                            false));
                    }
                }

                Aweme aweme = await App.Services.Video.GetDetailAsync(awemeId, false, token);
                if (aweme == null)
                {
                    throw new DouyinApiException(ApiError.Local(
                        ErrorKinds.NotFound, "aweme " + awemeId + " 没有返回内容", false));
                }

                await App.Services.Playback.StopAsync();
                _commentRows.Clear();
                _current = aweme;
                _resolved = null;
                _retriedAwemeId = null;
                Say("已打开 " + aweme.Id + "\n" + Describe(aweme));
            });
        }

        private static bool IsDigits(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] < '0' || text[index] > '9') return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // feed
        // ------------------------------------------------------------------

        private void OnLoadFeedClick(object sender, RoutedEventArgs e)
        {
            Run("加载 Feed", async token =>
            {
                FeedUpdate update = await App.Services.Feed.LoadInitialAsync(10, token);
                RenderFeed(update);
            });
        }

        private void OnNextPageClick(object sender, RoutedEventArgs e)
        {
            Run("加载下一页", async token =>
            {
                FeedUpdate update = await App.Services.Feed.LoadMoreAsync(10, token);
                RenderFeed(update);
            });
        }

        private void OnRefreshFeedClick(object sender, RoutedEventArgs e)
        {
            Run("刷新 Feed", async token =>
            {
                FeedUpdate update = await App.Services.Feed.RefreshAsync(10, token);
                RenderFeed(update);
            });
        }

        private void RenderFeed(FeedUpdate update)
        {
            _feedRows.Clear();

            IList<Aweme> items = App.Services.Feed.Items;
            for (int index = 0; index < items.Count; index++)
                _feedRows.Add(new FeedRow(items[index]));

            FeedText.Text = string.Format(
                "共 {0} 条；本次新增 {1}，去重 {2}，解析跳过 {3}，还有更多 {4}",
                items.Count,
                update == null || update.Added == null ? 0 : update.Added.Count,
                update == null ? 0 : update.DuplicatesRemoved,
                update == null ? 0 : update.SkippedByParser,
                App.Services.Feed.HasMore);
        }

        private void OnFeedSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var list = sender as ListView;
            var row = list == null ? null : list.SelectedItem as FeedRow;
            if (row == null) return;

            Run("选择视频", async token =>
            {
                await App.Services.Playback.StopAsync();
                _commentRows.Clear();
                _current = row.Item;
            _resolved = null;
            _retriedAwemeId = null;
            IdBox.Text = row.Item.Id;
            Say("已选中 " + row.Item.Id + "\n" + Describe(row.Item));
            });
        }

        // ------------------------------------------------------------------
        // playback
        // ------------------------------------------------------------------

        private void OnSearchVideosClick(object sender, RoutedEventArgs e) { StartSearch(false); }
        private void OnSearchUsersClick(object sender, RoutedEventArgs e) { StartSearch(true); }
        private void StartSearch(bool users)
        {
            string query = SearchBox.Text.Trim();
            if (query.Length == 0) { Say("请输入关键词"); return; }
            Run(users ? "搜索用户" : "搜索视频", async token =>
            {
                SearchText.Text = "正在搜索…";
                try
                {
                    var page = await App.Services.Api.SearchAsync(query, users, null, null, 10, token);
                    _searchVideos.Clear(); _searchUsers.Clear();
                    _searchQuery = query; _searchIsUsers = users;
                    RenderSearch(page);
                }
                catch { SearchText.Text = "搜索未完成，请查看上方提示；可扫码登录后重试。"; throw; }
            });
        }
        private void OnMoreSearchClick(object sender, RoutedEventArgs e)
        {
            if (!_searchHasMore || string.IsNullOrEmpty(_searchQuery)) { Say("没有可加载的下一页"); return; }
            Run("更多搜索结果", async token => RenderSearch(await App.Services.Api.SearchAsync(
                _searchQuery, _searchIsUsers, _searchCursor, _searchId, 10, token)));
        }
        private void RenderSearch(SearchPage page)
        {
            foreach (var video in page.Videos)
            {
                bool exists = false; foreach (var row in _searchVideos) if (row.Item.Id == video.Id) { exists = true; break; }
                if (!exists) _searchVideos.Add(new FeedRow(video));
            }
            foreach (var user in page.Users)
            {
                bool exists = false; foreach (var row in _searchUsers) if (row.Item.SecUid == user.SecUid) { exists = true; break; }
                if (!exists) _searchUsers.Add(new UserRow(user));
            }
            _searchHasMore = page.HasMore && !string.IsNullOrEmpty(page.Cursor) && page.Cursor != _searchCursor;
            _searchCursor = page.Cursor; _searchId = page.SearchId;
            SearchText.Text = string.Format("{0}：视频 {1}，用户 {2}；{3}", _searchQuery,
                _searchVideos.Count, _searchUsers.Count, _searchHasMore ? "可加载更多" : "没有更多结果");
        }
        private void OnOpenProfileClick(object sender, RoutedEventArgs e)
        {
            string uid = ProfileBox.Text.Trim(); Uri url;
            if (Uri.TryCreate(uid, UriKind.Absolute, out url))
            {
                if (url.Host != "www.douyin.com" || !url.AbsolutePath.StartsWith("/user/"))
                { Say("请输入抖音作者主页链接"); return; }
                uid = Uri.UnescapeDataString(url.AbsolutePath.Substring(6).Trim('/'));
            }
            OpenProfile(uid);
        }
        private void OnCurrentAuthorClick(object sender, RoutedEventArgs e)
        {
            OpenProfile(_current == null || _current.Author == null ? null : _current.Author.SecUid);
        }
        private void OnUserSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = SearchUsers.SelectedItem as UserRow;
            if (row != null) OpenProfile(row.Item.SecUid);
        }
        private void OpenProfile(string uid)
        {
            if (string.IsNullOrEmpty(uid)) { Say("请先选择视频作者，或输入主页链接"); return; }
            Run("作者主页", async token =>
            {
                var user = await App.Services.Api.GetUserAsync(uid, token);
                _profileUid = uid; _profileCursor = null; _profileHasMore = false; _profileVideos.Clear();
                ProfileBox.Text = uid;
                ProfileText.Text = string.Format("{0}  抖音号：{1}\n{2}\n关注 {3}  粉丝 {4}  作品 {5}",
                    user.Nickname, user.UniqueId, user.Signature, user.FollowingCount, user.FollowerCount, user.AwemeCount);
                try { RenderPosts(await App.Services.Api.GetUserPostsAsync(uid, null, 10, token)); }
                catch { ProfileText.Text += "\n资料已加载，作品列表未完成，请查看上方提示。"; throw; }
            });
        }
        private void OnMorePostsClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_profileUid)) { Say("请先打开作者主页"); return; }
            if (!_profileHasMore && _profileVideos.Count > 0) { Say("没有更多作品"); return; }
            Run("作者作品", async token => RenderPosts(await App.Services.Api.GetUserPostsAsync(
                _profileUid, _profileCursor, 10, token)));
        }
        private void RenderPosts(FeedPage page)
        {
            foreach (var video in page.Items)
            {
                bool exists = false; foreach (var row in _profileVideos) if (row.Item.Id == video.Id) { exists = true; break; }
                if (!exists) _profileVideos.Add(new FeedRow(video));
            }
            _profileHasMore = page.HasMore && !string.IsNullOrEmpty(page.Cursor) && page.Cursor != _profileCursor;
            _profileCursor = page.Cursor;
            Say("主页作品 " + _profileVideos.Count + (_profileHasMore ? "，可加载更多" : "，没有更多作品"));
        }

        private void OnPlayDownloadClick(object sender, RoutedEventArgs e)
        {
            Aweme aweme = _current;
            if (aweme == null)
            {
                Say("请先打开或选中一个视频");
                return;
            }

            Run("播放（下载）", async token =>
            {
                await App.Services.Playback.StopAsync();
                _resolved = await App.Services.Video.ResolveAsync(aweme, token);
                _codecUnverified = _resolved != null && _resolved.NeedsDecodeProbe;
                Say("选中的源：" + Describe(_resolved));
                await App.Services.Playback.PlayDownloadedAsync(aweme, App.Files.RootPath, token);
                Say("播放（下载）已开始：" + Describe(_resolved));
            });
        }

        private void OnPlayStreamClick(object sender, RoutedEventArgs e)
        {
            Aweme aweme = _current;
            if (aweme == null)
            {
                Say("请先打开或选中一个视频");
                return;
            }

            Run("播放（流式）", async token =>
            {
                await App.Services.Playback.StopAsync();
                _resolved = await App.Services.Video.ResolveAsync(aweme, token);
                _codecUnverified = _resolved != null && _resolved.NeedsDecodeProbe;
                await App.Services.Playback.PlayStreamingAsync(aweme, token);
                Say("播放（流式）已开始：" + Describe(_resolved));
            });
        }

        private async void OnStopClick(object sender, RoutedEventArgs e)
        {
            // Stop remains usable while a large download or media open is pending.
            if (_operationCts != null) _operationCts.Cancel();
            try
            {
                await App.Services.Playback.StopAsync();
                Say("已停止：" + App.Services.Playback.DescribeCache());
            }
            catch (Exception exception) { Say("停止失败：" + App.Describe(exception)); }
        }

        /// <summary>
        /// A decoder failure is the only evidence that a payload-labelled codec is
        /// wrong, so it is handed to the service that knows what to do with it. Guarded
        /// so a stream that keeps failing cannot turn into an infinite restart loop.
        /// </summary>
        private async void OnPlaybackError(string message)
        {
            Say("播放失败：" + message);
            if (_current == null) return;
            if (string.Equals(_retriedAwemeId, _current.Id, StringComparison.Ordinal)) return;
            if (!_codecUnverified) return;

            _retriedAwemeId = _current.Id;
            try
            {
                bool recovered = await App.Services.Playback
                    .RecoverFromDecodeFailureAsync(_current, _codecUnverified, _pageCts.Token);
                Say(recovered ? "已换用下一个候选源重试" : "没有可用的候选源了");
            }
            catch (Exception exception)
            {
                Say("解码恢复失败：" + App.Describe(exception));
            }
        }

        // ------------------------------------------------------------------
        // comments
        // ------------------------------------------------------------------

        private void OnLoadCommentsClick(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                Say("请先打开或选中一个视频");
                return;
            }

            string awemeId = _current.Id;
            Run("加载评论", async token =>
            {
                await App.Services.Comments.LoadFirstPageAsync(awemeId, 20, token);
                RenderComments(awemeId);
            });
        }

        private void OnMoreCommentsClick(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                Say("请先打开或选中一个视频");
                return;
            }

            string awemeId = _current.Id;
            Run("加载更多评论", async token =>
            {
                IList<Comment> added = await App.Services.Comments.LoadNextPageAsync(awemeId, 20, token);
                RenderComments(awemeId);
                Say("新增评论 " + (added == null ? 0 : added.Count));
            });
        }

        private void RenderComments(string awemeId)
        {
            _commentRows.Clear();
            IList<Comment> items = App.Services.Comments.Thread(awemeId).Items;
            for (int index = 0; index < items.Count; index++)
                _commentRows.Add(new CommentRow(items[index]));
        }

        private void OnPublishCommentClick(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                Say("请先打开或选中一个视频");
                return;
            }

            string text = (CommentBox.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                Say("请先输入评论内容");
                return;
            }

            string awemeId = _current.Id;
            Run("发表评论", async token =>
            {
                Comment published = await App.Services.Comments.PublishAsync(awemeId, text, token);
                CommentBox.Text = string.Empty;
                RenderComments(awemeId);
                Say(published == null ? "已提交（服务端未回显）" : "已发表：" + published.Id);
            });
        }

        // ------------------------------------------------------------------
        // session
        // ------------------------------------------------------------------

        private async void OpenLoginIfRequested()
        {
            string path = Path.Combine(App.Files.RootPath, "login-check.txt");
            if (!File.Exists(path)) return;
            File.Delete(path);
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => OnQrLoginClick(null, null));
        }

        private void OnQrLoginClick(object sender, RoutedEventArgs e)
        {
            if (_busy) { Say("请等待当前操作完成，或点击取消"); return; }
            OnQrApiLoginClick(sender, e);
        }

        private void OnQrApiLoginClick(object sender, RoutedEventArgs e)
        {
            Run("扫码登录", async token =>
            {
                _loginSession = null;
                QrImage.Source = null;
                try
                {
                    _loginSession = await App.Services.Api.CreateLoginSessionAsync(token);
                    await RenderLoginAsync(token);
                    while (_loginSession != null && !_loginSession.IsTerminal)
                    {
                        int interval = _loginSession.PollIntervalMs > 0 ? _loginSession.PollIntervalMs : 2000;
                        await Task.Delay(interval, token);
                        _loginSession = await App.Services.Api.PollLoginSessionAsync(_loginSession, token);
                        await RenderLoginAsync(token);
                    }
                    await App.Services.Session.RefreshAssessmentAsync(token);
                    RenderSession();
                    Say(_loginSession == null ? "登录会话为空" : "登录结束，状态 " + _loginSession.Status);
                }
                catch (Exception exception)
                {
                    bool cancelled = exception is OperationCanceledException;
                    var api = exception as DouyinApiException;
                    if (_loginSession == null) _loginSession = new LoginSessionModel();
                    _loginSession.Status = LoginStatus.Failed;
                    _loginSession.StatusMessage = cancelled ? "扫码登录已取消" : App.Describe(exception);
                    _loginSession.ErrorKind = cancelled ? "cancelled" : (api == null ? "internal" : api.Kind);
                    _loginSession.Token = null;
                    _loginSession.QrUrl = null;
                    _loginSession.QrImageBase64 = null;
                    await RenderLoginAsync(CancellationToken.None);
                    throw;
                }
            });
        }

        private async Task RenderLoginAsync(CancellationToken token)
        {
            if (_loginSession == null)
            {
                LoginText.Text = "登录：未开始";
                return;
            }

            try
            {
                var report = new Newtonsoft.Json.Linq.JObject {
                    { "package", Windows.ApplicationModel.Package.Current.Id.FullName },
                    { "source", "native-passport-desktop" }, { "status", _loginSession.Status },
                    { "hasQrImage", !_loginSession.IsTerminal && !string.IsNullOrEmpty(_loginSession.QrImageBase64) },
                    { "expiresAtMs", _loginSession.ExpiresAtMs }, { "errorKind", _loginSession.ErrorKind },
                    { "response", _loginSession.ResponseDiagnostic },
                    { "isLoggedIn", App.Services.Session.IsLoggedIn }, { "updatedAtUtc", DateTimeOffset.UtcNow.ToString("o") }
                };
                File.WriteAllText(Path.Combine(App.Files.RootPath, "login-check-report.json"), report.ToString());
            }
            catch { }
            string label = _loginSession.Status;
            switch (_loginSession.Status)
            {
                case "WaitingForScan": label = "请用已登录抖音的另一台手机扫描二维码"; break;
                case "Scanned": label = "已扫描，请在抖音中确认登录"; break;
                case "WaitingForConfirmation": label = "等待确认登录"; break;
                case "Confirmed": label = "已登录"; break;
                case "Expired": label = "二维码已过期，请重新获取"; break;
                case "Failed": label = "登录失败，请重新获取"; break;
            }
            LoginText.Text = label + " " + (_loginSession.StatusMessage ?? string.Empty);
            if (_loginSession.IsTerminal) { QrImage.Source = null; return; }

            string base64 = _loginSession.QrImageBase64;
            if (string.IsNullOrEmpty(base64)) return;

            int comma = base64.IndexOf(',');
            if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                base64 = base64.Substring(comma + 1);

            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                using (var memory = new MemoryStream(bytes))
                {
                    var image = new BitmapImage();
                    await image.SetSourceAsync(memory.AsRandomAccessStream());
                    QrImage.Source = image;
                }
            }
            catch (Exception exception)
            {
                App.Log.Warn(DiagCategory.Login,
                    "the QR image could not be decoded: " + exception.Message);
            }
        }

        private void OnImportCookieClick(object sender, RoutedEventArgs e)
        {
            string text = (CookieBox.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                Say("请先粘贴 Cookie");
                return;
            }

            Run("导入 Cookie", async token =>
            {
                // The pasted text is a credential: it is handed straight to the session
                // store and never logged. Only the report's safe projection is shown --
                // the same three fields the Core layer logs -- so a future change to the
                // Python report cannot start leaking values into the UI.
                var report = await App.Services.Session.ImportCookiesAsync(text, token);
                CookieBox.Text = string.Empty;
                RenderSession();
                Say(string.Format(
                    "导入结果：state={0} imported={1} markers={2}",
                    JsonValue.GetString(report, "state", "unknown"),
                    JsonValue.GetInt(report, "importedCount", 0),
                    string.Join(",", JsonValue.GetStrings(report, "sessionMarkers"))));
            });
        }

        private void OnLogoutClick(object sender, RoutedEventArgs e)
        {
            Run("登出", async token =>
            {
                BrowserSession.Clear();
                await App.Services.Session.LogoutAsync(token);
                RenderSession();
                Say("已登出");
            });
        }

        private void OnCheckSessionClick(object sender, RoutedEventArgs e)
        {
            Run("检查会话", async token =>
            {
                SessionAssessment assessment = await App.Services.Api.CheckSessionAsync(token);
                RenderSession();
                Say(assessment == null
                    ? "会话检查没有返回结果"
                    : string.Format("状态 {0}，cookie {1}", assessment.State, assessment.CookieCount));
            });
        }

        // ------------------------------------------------------------------
        // diagnostics
        // ------------------------------------------------------------------

        private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DiagnosticsPage));
        }

        // ------------------------------------------------------------------
        // rendering helpers
        // ------------------------------------------------------------------

        private static string Describe(Aweme aweme)
        {
            if (aweme == null) return "(null)";

            string author = aweme.Author == null
                ? "?"
                : (aweme.Author.Nickname ?? aweme.Author.UniqueId ?? "?");
            string statistics = aweme.Statistics == null
                ? "-"
                : string.Format("赞 {0} 评论 {1} 播放 {2}",
                    aweme.Statistics.DiggCount,
                    aweme.Statistics.CommentCount,
                    aweme.Statistics.PlayCount);

            return string.Format(
                "{0}\n作者 {1}　{2}\n时长 {3} ms　{4}\n源：{5}\n分享：{6}",
                Shorten(aweme.Desc, 80),
                author,
                statistics,
                aweme.DurationMs,
                aweme.IsImage ? "图文" : "视频",
                SourcesHint(aweme),
                aweme.ShareUrl ?? "(无)");
        }

        private static string Describe(ResolvedVideoSource resolved)
        {
            if (resolved == null) return "(未解析)";
            VideoSource source = resolved.Source;
            if (source == null) return "(没有可用的源)";

            return string.Format(
                "{0} / {1} / {2}x{3} / {4}　codecEvidence={5}　候选 {6}　需要探测 {7}\n{8}",
                source.Quality,
                source.Codec,
                source.Width,
                source.Height,
                source.SizeBytes.HasValue ? (source.SizeBytes.Value / 1024) + " KB" : "size?",
                source.CodecEvidence ?? "(无)",
                resolved.Ranked == null ? 0 : resolved.Ranked.Count,
                resolved.NeedsDecodeProbe,
                Redactor.Url(source.Url));
        }

        private static string SourcesHint(Aweme aweme)
        {
            if (aweme == null || aweme.Video == null || aweme.Video.Sources == null
                || aweme.Video.Sources.Count == 0)
                return "(无)";

            var parts = new List<string>();
            for (int index = 0; index < aweme.Video.Sources.Count && index < 4; index++)
            {
                VideoSource source = aweme.Video.Sources[index];
                parts.Add(source.Quality + "/" + source.Codec);
            }
            return string.Join(" ", parts.ToArray());
        }

        private static string Shorten(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return "(无描述)";
            string single = text.Replace("\r", " ").Replace("\n", " ");
            return single.Length <= limit ? single : single.Substring(0, limit) + "...";
        }

        private static string[] ToArray(IList<string> items)
        {
            var copy = new string[items.Count];
            for (int index = 0; index < items.Count; index++) copy[index] = items[index];
            return copy;
        }

        /// <summary>One feed row. A property, not a field: XAML can only bind to
        /// properties.</summary>
        public sealed class FeedRow
        {
            public FeedRow(Aweme item)
            {
                Item = item;
                Summary = item == null
                    ? "(空条目)"
                    : string.Format(
                        "{0}\n作者 {1}　赞 {2}　评论 {3}\n{4}\nid={5}　{6} ms　{7}",
                        Shorten(item.Desc, 60),
                        item.Author == null ? "?" : (item.Author.Nickname ?? "?"),
                        item.Statistics == null ? 0 : item.Statistics.DiggCount,
                        item.Statistics == null ? 0 : item.Statistics.CommentCount,
                        SourcesHint(item),
                        item.Id,
                        item.DurationMs,
                        item.IsImage ? "图文" : "视频");
            }

            public Aweme Item { get; private set; }

            public string Summary { get; private set; }
        }

        /// <summary>One comment row.</summary>
        public sealed class UserRow
        {
            public UserRow(UserProfile user)
            {
                Item = user;
                Summary = string.Format("{0}　抖音号 {1}\n{2}", user.Nickname, user.UniqueId, user.Signature);
            }
            public UserProfile Item { get; private set; }
            public string Summary { get; private set; }
        }

        public sealed class CommentRow
        {
            public CommentRow(Comment item)
            {
                Item = item;
                Summary = item == null
                    ? "(空评论)"
                    : string.Format(
                        "{0}　赞 {1}　回复 {2}{3}\n{4}",
                        item.Author == null ? "?" : (item.Author.Nickname ?? "?"),
                        item.DiggCount,
                        item.ReplyCount,
                        item.IsAuthor ? "　[作者]" : string.Empty,
                        Shorten(item.Text, 120));
            }

            public Comment Item { get; private set; }

            public string Summary { get; private set; }
        }
    }
}
