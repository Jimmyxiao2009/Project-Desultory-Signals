using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;

namespace DesultorySignals.Views
{
    public sealed partial class LoginPage : Page
    {
        private const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        private CancellationTokenSource _lifetime;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private bool _checking, _done;
        private DateTimeOffset _opened;
        private readonly JObject _report = new JObject();
        public LoginPage() { InitializeComponent(); _timer.Tick += OnTick; }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e); _lifetime = new CancellationTokenSource(); _done = false;
            _opened = DateTimeOffset.UtcNow; _report["package"] = Package.Current.Id.FullName;
            _report["startedAtUtc"] = _opened.ToString("o"); _report["status"] = "loading";
            NavigateDesktopLogin(); _timer.Start(); SaveReport();
            App.Log.Info(DiagCategory.Login, "official browser QR login opened");
        }
        private void NavigateDesktopLogin()
        {
            // A plain Navigate uses the phone UA and selects SMS login. Always use
            // the desktop header on both the initial visit and explicit reloads.
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://www.douyin.com/login_page"));
            request.Headers.TryAppendWithoutValidation("User-Agent", DesktopUserAgent);
            request.Headers.TryAppendWithoutValidation("Cache-Control", "no-cache");
            request.Headers.TryAppendWithoutValidation("Pragma", "no-cache");
            _report["requestUserAgent"] = DesktopUserAgent;
            _report["desktopUserAgentRequested"] = true;
            LoginBrowser.NavigateWithHttpRequestMessage(request);
        }
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _timer.Stop(); _lifetime.Cancel(); LoginBrowser.Stop(); base.OnNavigatedFrom(e);
        }
        private void OnNavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            Uri uri = args.Uri;
            if (uri != null && (uri.Scheme != "https" || !(uri.Host == "douyin.com" || uri.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase))))
            { args.Cancel = true; LoginStatus.Text = "登录页尝试跳转到未支持的地址，请刷新重试。"; }
        }
        private void OnNavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            _report["navigationSuccess"] = args.IsSuccess;
            _report["navigationError"] = args.IsSuccess ? null : args.WebErrorStatus.ToString();
            _report["status"] = args.IsSuccess ? "waiting" : "navigation_failed";
            LoginStatus.Text = args.IsSuccess ? "正在等待电脑版扫码界面加载…" : "登录页面加载失败：" + args.WebErrorStatus;
            SaveReport();
        }
        private async void OnTick(object sender, object e) { await CheckAsync(); }
        private async void OnComplete(object sender, RoutedEventArgs e) { await CheckAsync(); }
        private async Task CheckAsync()
        {
            if (_checking || _done || _lifetime.IsCancellationRequested) return;
            _checking = true;
            try
            {
                // Only presence and rendering metrics are written to diagnostics.
                JArray cookies = BrowserSession.Read();
                _report["cookieCount"] = cookies.Count; _report["hasSessionCookie"] = BrowserSession.HasSession(cookies);
                try
                {
                    string metrics = await LoginBrowser.InvokeScriptAsync("eval", new[] {
                        "JSON.stringify({userAgent:navigator.userAgent,mobileUserAgent:/Windows Phone|Mobile|Android|iPhone/i.test(navigator.userAgent),hasScanText:/扫码|扫描二维码/.test(document.body?document.body.innerText:''),hasSmsText:/手机验证码|短信验证码|验证码登录/.test(document.body?document.body.innerText:''),readyState:document.readyState,bodyLength:document.body?document.body.innerText.length:0,images:document.images.length,qrImagePresent:!!document.querySelector('img[src^=\"data:image\"]'),mountChildren:document.getElementById('login')?document.getElementById('login').children.length:0,textEncoder:typeof TextEncoder,promise:typeof Promise})" });
                    _report["page"] = JObject.Parse(metrics);
                }
                catch { _report["scriptReadable"] = false; }
                JObject metricsPage = _report["page"] as JObject;
                if (metricsPage != null && (bool?)metricsPage["qrImagePresent"] == true)
                    LoginStatus.Text = "请用已登录抖音的另一台手机扫码，并确认登录；应用会自动保存会话。";
                else if (metricsPage != null && (bool?)metricsPage["hasSmsText"] == true)
                    LoginStatus.Text = "当前仍显示验证码登录，正在检查电脑版扫码入口。";
                if (_lifetime.IsCancellationRequested) return;
                if (BrowserSession.HasSession(cookies))
                {
                    await App.Services.Session.ImportCookiesAsync(cookies.ToString(Formatting.None), _lifetime.Token);
                    var state = await App.Services.Api.CheckSessionAsync(_lifetime.Token);
                    if (!App.Services.Session.IsLoggedIn) throw new InvalidOperationException("会话未通过检查");
                    _done = true; _timer.Stop(); _report["status"] = "confirmed"; SaveReport();
                    App.Log.Info(DiagCategory.Login, "official browser session saved and validated");
                    LoginStatus.Text = "登录成功，已保存会话";
                    if (Frame.CanGoBack) Frame.GoBack();
                    return;
                }
                if ((DateTimeOffset.UtcNow - _opened).TotalSeconds > 25)
                {
                    JObject page = _report["page"] as JObject;
                    if (page != null && (int?)page["mountChildren"] == 0)
                    { _report["status"] = "page_not_rendered"; LoginStatus.Text = "官网登录界面未能显示，请刷新；可返回后使用 Cookie 导入。"; }
                }
                SaveReport();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                LoginStatus.Text = "登录尚未完成：" + App.Describe(exception);
                _report["status"] = "session_check_failed"; SaveReport();
                App.Log.Warn(DiagCategory.Login, "browser login session check failed: " + App.Describe(exception));
            }
            finally { _checking = false; }
        }
        private void SaveReport()
        {
            try { _report["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("o"); File.WriteAllText(Path.Combine(App.Files.RootPath, "login-check-report.json"), _report.ToString()); }
            catch { }
        }
        private void OnBack(object sender, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); }
        private void OnReload(object sender, RoutedEventArgs e)
        { _opened = DateTimeOffset.UtcNow; LoginStatus.Text = "正在重新加载…"; NavigateDesktopLogin(); }
    }
}
