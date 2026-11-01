using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace DesultorySignals.Views
{
    /// <summary>
    /// Everything the shell knows, on one page, plus an export the user can pull off
    /// the device.
    /// </summary>
    /// <remarks>
    /// Two rules hold here.
    ///
    /// First, the page only *reads*: the values come from
    /// <see cref="Core.Services.ServiceHost.DiagnosticsAsync"/> (which describes the
    /// session in its redacted form) and from <see cref="DiagLog.Dump"/> (which scrubs
    /// every message before storing it). Nothing on this page can be the reason a
    /// credential ends up in a file.
    ///
    /// Second, the export writes exactly those two strings plus the startup report and
    /// the runtime description. It never concatenates a header, a cookie or an identity
    /// document -- the one way a redaction guarantee gets broken is by adding "just one
    /// more useful field" at the export site.
    /// </remarks>
    public sealed partial class DiagnosticsPage : Page
    {
        private CancellationTokenSource _pageCts = new CancellationTokenSource();

        public DiagnosticsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_pageCts.IsCancellationRequested)
            {
                _pageCts.Dispose();
                _pageCts = new CancellationTokenSource();
            }
            Refresh();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _pageCts.Cancel();
        }

        private async void Refresh()
        {
            SummaryText.Text = "读取中...";
            try
            {
                string text = await BuildExportTextAsync(_pageCts.Token);
                DumpText.Text = text;
                SummaryText.Text = "已刷新";
                PathText.Text = "导出目录：" + App.Files.RootPath;
            }
            catch (OperationCanceledException)
            {
                SummaryText.Text = "已取消";
            }
            catch (Exception exception)
            {
                SummaryText.Text = "读取诊断失败：" + App.Describe(exception);
            }
        }

        private async Task<string> BuildExportTextAsync(CancellationToken cancellationToken)
        {
            var text = new StringBuilder();

            BootstrapReport report = App.Bootstrap;
            text.AppendLine("== 启动报告 ==");
            text.AppendLine(report == null ? "(尚无启动报告)" : report.Summary());
            if (report != null)
            {
                text.AppendLine("engineStarted      : " + report.EngineStarted);
                text.AppendLine("runtimeInitialized : " + report.RuntimeInitialized);
                text.AppendLine("bridgeReady        : " + report.BridgeReady);
                text.AppendLine("selfTestOk         : " + report.SelfTestOk);
                text.AppendLine("runtime            : " + (report.RuntimeDescribe ?? "(未提供)"));
                text.AppendLine("lastNativeError    : " + (report.LastNativeError ?? "(无)"));
                text.AppendLine("selfTestError      : " + (report.SelfTestError ?? "(无)"));
                text.AppendLine("session            : " + (report.SessionState ?? "(未知)"));
                text.AppendLine("bootstrapError     : " + (report.Error ?? "(无)"));
            }

            text.AppendLine();
            text.AppendLine("== 运行时 ==");
            text.AppendLine(App.Runtime == null ? "(无运行时)" : App.Runtime.Describe());
            text.AppendLine("installedLocation  : " + (App.RuntimeRoot ?? "(未知)"));
            text.AppendLine("localSite          : " + (App.LocalSitePath ?? "(未知)"));

            text.AppendLine();
            text.AppendLine("== 服务 ==");
            try
            {
                JObject diagnostics = await App.Services.DiagnosticsAsync(cancellationToken);
                text.AppendLine(diagnostics.ToString(Formatting.Indented));
            }
            catch (Exception exception)
            {
                text.AppendLine("(诊断调用失败：" + App.Describe(exception) + ")");
            }

            text.AppendLine();
            text.AppendLine("== 日志（DiagLog 已脱敏）==");
            text.AppendLine(App.Log == null ? "(无日志)" : App.Log.Dump());

            return text.ToString();
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            const string path = "diagnostics-export.txt";
            try
            {
                string text = await BuildExportTextAsync(_pageCts.Token);
                await App.Files.WriteTextAsync(path, text, _pageCts.Token);

                string absolute = App.Files.AbsolutePathOf(path);
                int bytes = Encoding.UTF8.GetByteCount(text);
                PathText.Text = "已导出 " + bytes + " 字节：" + absolute;
                App.Log.Info(DiagCategory.Storage, "diagnostics exported", new JObject
                {
                    { "path", absolute },
                    { "bytes", bytes }
                });
            }
            catch (OperationCanceledException)
            {
                SummaryText.Text = "导出已取消";
            }
            catch (Exception exception)
            {
                string message = "导出失败：" + App.Describe(exception);
                SummaryText.Text = message;
                App.Log.Error(DiagCategory.Storage, message);
            }
        }

        private async void OnPythonSelfTestClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // Both probes touch the interpreter, so both go through the engine
                // thread; calling them from here directly is exactly what the ABI's
                // wrong-thread guard exists to catch.
                int selfTest = await App.Engine.InvokeAsync(
                    delegate { return App.Runtime.SelfTest(); }, _pageCts.Token);

                int code = await App.Engine.InvokeAsync(
                    delegate
                    {
                        return App.Runtime.RunUtf8(
                            "import sys\nprint(sys.version)\nprint(sys.path)\n");
                    },
                    _pageCts.Token);

                string output = await App.Engine.InvokeAsync(
                    delegate { return App.Runtime.LastOutput(); }, _pageCts.Token);

                SummaryText.Text = "PyUwpSelfTest=" + selfTest + "，RunUtf8=" + code
                    + "（" + NativeRuntime.Describe(code) + "）";
                DumpText.Text = "== RunUtf8 输出 ==\n" + (output ?? "(空)") + "\n\n" + DumpText.Text;
            }
            catch (Exception exception)
            {
                SummaryText.Text = "Python 自检失败：" + App.Describe(exception);
            }
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
