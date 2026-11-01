# HANDOFF — 给第二阶段 UI Agent

**只读本文档就能接上。** 本文档描述第一阶段的成果边界、调用方式，以及**哪些代码不许改**。

> **最新修复状态（2026-10-07）**：启动闪退已修复，ARM32 调试壳已在 Windows 10 Mobile **15254.603** 正常运行。
> 根因证据与修复见 [docs/STARTUP-DIAGNOSIS.md](docs/STARTUP-DIAGNOSIS.md)：原生启动器缺少已声明的 Retail VCLibs 包依赖。
> 真机已取得 `ttwid`，CPython 桥接及启动自检正常；Feed、详情、评论与回复、下载及流式视频画面已取得真机证据，用户确认画面正常。
> 搜索和主页入口已加入：游客视频搜索明确要求登录，作者资料成功，作品列表尚未取得成功响应。
> 扫码正在使用原生二维码 + 官方桌面 Passport 接口，主机真实请求已验证生成与未扫码轮询；用户扫码后被拒绝，提示“系统繁忙，请重启应用或刷新页面后重试”；.12 日志确认 error_code=2156，无验证挑战，并修复失败后仍显示等待；.13 已修正 is_frontier 与会话关联头并部署，真机复查仍为 2156，参数修正未解决拒绝；未扫描对照连续四次成功且 CSRF 匹配。扫码登录及登录后的功能未通过验证。
> 普通 WebView 会进入手机验证码页；即使 HTTP 头设置电脑 UA，`navigator.userAgent` 仍为手机。该方案已从扫码入口移除。
> 新增网页端检查：电脑 Edge 的官网 login_page 已显示二维码；网页 HTTP API aid=6383 能生成二维码，但未扫描轮询返回 4031。浏览器调试连接持续超时，用户已报告在电脑完成扫码后的验证码验证；手机 .13 于 13:54:03Z 重试仍为 2156、游客、无已支持验证挑战，未转移电脑凭据。详见 docs/QR-LOGIN.md。
> 部署/采集脚本：[scripts/device-check.py](scripts/device-check.py)。历史 483 项桌面断言未在本次修改后重新运行。

---

## 0. 三十秒版本

- 核心逻辑全部在 `src/DesultorySignals.Core/`（netstandard1.4，已 `dotnet build` 通过，0 错误）。
- 协议层全部在 `python/douyin/`（仅标准库，`python/tests/run_tests.py` **187/187 通过**）。
- 你只需要改 `app/DesultorySignals/` 下的 **XAML 和 CodeBehind**。
- **唯一入口是 `ServiceHost`。** 不要直接 new 任何 Service，不要碰 `DouyinApiClient` 以下的东西。
- 所有异常都是 `DouyinApiException`，用 `error.Kind` 分支，用 `error.Describe()` 显示。

---

## 1. 已存在的 Service 与调用方式

组合根是 `ServiceHost`。它的构造参数就是**设备必须提供的全部平台能力**：

```csharp
var host = new ServiceHost(python, transport, store, media, log);
//  python    : PythonProtocolClient   （CPython 引擎线程 + ABI）
//  transport : IHttpTransport          （TLS/socket）
//  store     : IFileStore              （应用私有存储）
//  media     : IMediaPlayerHost        （MediaElement）
//  log       : DiagLog
```

启动：

```csharp
SessionAssessment session = await host.InitializeAsync(
    bootstrapGuestSession: true,   // 首次运行要 true：拿 ttwid
    cancellationToken);
// session.IsLoggedIn → 是否已登录
```

挂起：停止播放、在有限时间内保存 History/Session；保留 `ServiceHost` 和 CPython 实例。恢复时检查会话。不要在 `Application.Suspending` 中终结解释器或重建组合根。

### 服务清单

| 属性 | 类型 | 职责 |
|---|---|---|
| `host.Session` | `SessionManager` | 身份 + Cookie + 持久化 + 登录态事件。**唯一**读写 session 文件的地方 |
| `host.Api` | `DouyinApiClient` | 端点适配器。**唯一**知道 endpoint 的类 |
| `host.Feed` | `FeedService` | Feed 首页/刷新/分页/去重 |
| `host.Video` | `VideoService` | aweme detail + 可播放源选择 + 源刷新 |
| `host.Comments` | `CommentService` | 评论读取/分页/回复/发布 |
| `host.Playback` | `PlaybackService` | 播放链路（下载式 + 流式） |
| `host.Cache` | `CacheService` | 元数据 + 图片缓存 |
| `host.History` | `HistoryService` | 本地历史 |
| `host.Log` | `DiagLog` | 诊断日志 |
| `host.DiagnosticsAsync(ct)` | | 诊断页数据（已脱敏） |
| `host.ReleasePageResourcesAsync()` | | **页面离开必调**（见 §5） |

---

## 2. Model 定义

全部在 `Core/Models/Models.cs`。字段名与 Python 契约逐字一致。

```csharp
class Aweme {
    string Id; string Desc; long? CreateTime;
    Author Author; VideoInfo Video; Statistics Statistics;
    bool IsImage; IList<ImageAsset> Images; string ShareUrl; int DurationMs;
    string BestCoverUrl;   // 列表用封面：Video.CoverUrl → Images[0]
}

class Author { string Id, SecUid, Nickname, AvatarUrl, UniqueId, Signature;
               string DisplayName; }   // Nickname → @UniqueId → "未知用户"

class VideoInfo { int Width, Height, DurationMs;
                  string CoverUrl, DynamicCoverUrl, Format; bool HasWatermark;
                  IList<VideoSource> Sources; bool HasAnySource; }

class VideoSource { string Url, OriginalUrl, Codec, CodecEvidence, Container, Quality, GearName;
                    int Width, Height; int? Bitrate; long? SizeBytes; }
                    // Codec: "h264" | "hevc" | "av1" | "unknown"
                    // Quality: "1080p" "720p" "540p" "480p" "360p" "240p" "unknown"

class Statistics { int DiggCount, CommentCount, ShareCount, CollectCount, PlayCount; }

class Comment { string Id, Text; long? CreateTime; int DiggCount, ReplyCount;
                Author Author; string ReplyToUsername, ReplyToCommentId;
                int Level; int Status; bool IsAuthor, IsPinned; bool HasReplies; }

class FeedPage    { IList<Aweme> Items; string Cursor; bool HasMore;
                    string SourceKey; int Skipped; string LogId; }
class CommentPage { IList<Comment> Items; string Cursor; bool HasMore; int Total; int Skipped; }
class UserProfile { ... FollowerCount FollowingCount AwemeCount ... }
class SessionAssessment { string State;   // "guest" | "logged_in"
                          IList<string> SessionMarkers; int CookieCount; bool IsLoggedIn; }
class LoginSessionModel { string Id, Status, QrUrl, QrImageBase64, StatusMessage, ErrorKind, Token;
                          long ExpiresAtMs; int PollIntervalMs; bool IsTerminal; }
class HistoryEntry { string AwemeId, Desc, AuthorName, CoverUrl;
                     long WatchedAtMs; int PositionMs, DurationMs; }
class ShareParseResult { string Kind;  // "aweme_id" | "redirect" | "unsupported"
                         string AwemeId, NextUrl, Source, Reason, Input; bool NeedsRedirect; }
```

**所有 Model 都是普通 POCO，没有 `INotifyPropertyChanged`。** 如果 UI 要绑定，请自己包一层
ViewModel —— 不要在 Core 的 Model 上加通知逻辑，那会让它们不再可序列化、不再可测试。

---

## 3. Feed 分页接口

```csharp
IList<Aweme> items = host.Feed.Items;    // 快照，可直接绑
int  count   = host.Feed.Count;
bool more    = host.Feed.HasMore;
string cursor = host.Feed.Cursor;        // 不透明，别解析、别自增

Task<FeedUpdate> host.Feed.LoadInitialAsync(int count, CancellationToken ct);
Task<FeedUpdate> host.Feed.RefreshAsync    (int count, CancellationToken ct);
Task<FeedUpdate> host.Feed.LoadMoreAsync   (int count, CancellationToken ct);
void             host.Feed.Reset();
event Action<FeedUpdate> host.Feed.Updated;

class FeedUpdate { IList<Aweme> Added; int DuplicatesRemoved; bool HasMore;
                   string Cursor; int SkippedByParser; int TotalItems; }
```

行为保证（都有测试）：

- **去重在这里做完**。同一 aweme 跨页/跨刷新重复出现时不会进列表，`DuplicatesRemoved` 计数。
- **并发安全**。内部 `SemaphoreSlim` 串行化；快速滚动触发两次 LoadMore 时第二次会用第一次
  产生的 cursor，不会丢页。
- **失败不破坏列表**。抛 `DouyinApiException`，`Items` 原样不动 —— 所以你可以先渲染旧的再报错。
- 首次加载前 `Items` 为空、`HasMore == true`。

UI 用法建议：`Updated` 事件里只 `Added` 追加到你的 `ObservableCollection`，避免整表重刷。

---

## 4. Comment API

```csharp
CommentThread thread = host.Comments.Thread(awemeId);  // 按 aweme 缓存的线程对象
IList<Comment> thread.Items;   int thread.Count;   string thread.Cursor;
bool thread.HasMore;           int thread.Total;   void thread.Reset();

Task<IList<Comment>> host.Comments.LoadFirstPageAsync(awemeId, count, ct);
Task<IList<Comment>> host.Comments.LoadNextPageAsync (awemeId, count, ct);
Task<CommentPage>    host.Comments.LoadRepliesAsync  (awemeId, commentId, cursor, count, ct);

Task<Comment> host.Comments.PublishAsync(awemeId, text, ct);
Task<Comment> host.Comments.ReplyAsync(awemeId, commentId, text, replyToReplyId, ct);
void          host.Comments.Clear();
```

- 返回的 `IList<Comment>` 是**本次新增**的，不是全量。
- `LoadNextPageAsync` 在 `HasMore == false` 时直接返回空，不请求。
- **一级评论与二级回复分开**。回复用 `LoadRepliesAsync`，它返回一页 `CommentPage`（带自己的 cursor），
  **不会**并入 `thread`。这是刻意的：回复属于它自己的父评论，塞进一级列表是撒谎。
- 发布成功后一级评论被**插到列表最前面**（`thread.Items[0]`）；回复不插入。
- 发布是**有保护的**：60 秒内相同 `(aweme, text, reply 目标)` 会被本地拒绝为
  `invalid_input`（`detail.reason == "duplicate-suppressed"`）。所以连点两次是安全的，
  但你要给用户"已提交"的反馈，否则第二次的拒绝会让人困惑。

---

## 5. PlaybackService 接口

```csharp
enum PlaybackState { Idle, Resolving, Downloading, Preparing, Playing, Paused, Failed }

PlaybackState host.Playback.State;
Aweme         host.Playback.Current;
ApiError      host.Playback.LastError;      // 失败时非空
string        host.Playback.DescribeCache();// 诊断用：命中率/字节数
event Action<PlaybackState> host.Playback.StateChanged;
event Action<ApiError>      host.Playback.Failed;

// 路径 A（Phase 3）：整文件下载到私有存储再播。保留它用于隔离故障。
Task<string> host.Playback.DownloadToLocalAsync(Aweme aweme, CancellationToken ct);
Task host.Playback.PlayDownloadedAsync(Aweme aweme, string absoluteMediaRoot, CancellationToken ct);

// 路径 B（Phase 4）：CDN range 流式播放
Task host.Playback.PlayStreamingAsync(Aweme aweme, CancellationToken ct);

Task host.Playback.StopAsync();
Task<bool> host.Playback.RecoverFromDecodeFailureAsync(
        Aweme aweme, bool codecWasUnverified, CancellationToken ct);
```

**UI 必须做的事：**

1. 切换视频前 `await host.Playback.StopAsync()`。
2. `IMediaPlayerHost` 实现里监听 `MediaElement.MediaFailed`，然后调用
   `RecoverFromDecodeFailureAsync(aweme, codecWasUnverified: true, ct)`。
   `codecWasUnverified` 从 `VideoService` 的解析结果来（`NeedsDecodeProbe`）；
   如果 payload 明确写了 H.264 却解码失败，传 `false` —— 那说明流不是它声称的东西，
   换下一个候选只是浪费带宽。
3. 页面离开/挂起时 `await host.ReleasePageResourcesAsync()`。
   **这是"连续刷视频不增长内存"验收项的落点**：它停播放器、清评论线程、重置 feed。
4. URL 过期**不用 UI 操心**：`RemoteRandomAccessByteSource` 会回调
   `VideoService.RefreshSourcesAsync` 换新 URL 并丢弃缓存，最多两次。

---

## 6. Account API 与登录

### 查询与登出

```csharp
SessionAssessment host.Session.Assessment;   // 每次从 cookie jar 重算，不是缓存标志
bool              host.Session.IsLoggedIn;
event Action<SessionAssessment> host.Session.SessionStateChanged;  // 只在状态真的变了时触发

Task<JObject> host.Session.ImportCookiesAsync(string cookieText, CancellationToken ct);
Task<JObject> host.Session.LogoutAsync(CancellationToken ct);
Task<JObject> host.Session.ResetIdentityAsync(CancellationToken ct);
Task<JObject> host.Session.DescribeRedactedAsync(CancellationToken ct);   // 诊断页用
Task          host.Session.FlushAsync(CancellationToken ct);
```

### 登录方式：**Cookie 导入是唯一可用路径**

```csharp
// 用户可以粘贴三种格式，全部支持：
//   1. 原始 Cookie 头：  sessionid=xxx; ttwid=yyy
//   2. JSON 数组：       [{"name":"sessionid","value":"xxx","domain":".douyin.com"}]
//   3. Netscape cookies.txt
JObject report = await host.Session.ImportCookiesAsync(pastedText, ct);
// report["state"] → "logged_in" | "guest"
// report["sessionMarkers"] → ["sessionid", "sid_tt", ...]
// report["importedCount"], report["mirroredFingerprint"]
```

导入时会**把指纹类 cookie 镜像到身份上**（`ttwid`、`s_v_web_id`），
所以签名、指纹、cookie jar 三者不会互相矛盾 —— 这正是 brief 点名要避免的失败模式。

**扫码登录已不可用，且是实测结论**：

```csharp
await host.Api.CreateLoginSessionAsync(ct);
// → 抛 DouyinApiException，Kind == "signer_unavailable"
//   Detail["alternative"] == "login.importCookie"
```

原因：`GET https://sso.douyin.com/get_qrcode/` 现在返回一个 9938 字节的 JS 应用页，
不再是 QR payload。状态机、解析器、测试都还在（`QR_FLOW_AVAILABLE = False` 是唯一开关），
Douyin 恢复该流程时改一行即可。

**UI 应该**：把 Cookie 导入做成主路径，给出"从已登录的桌面浏览器复制 cookie"的说明。
不要把二维码入口做成主按钮然后永远失败。

### 会话有效性

```csharp
SessionAssessment s = await host.Api.CheckSessionAsync(ct);
// 服务端拒绝时抛 Kind == "session_expired"
```

`Assessment` 每次从 cookie jar 重算，**所以 C# 里不会有一个"以为登录了其实没有"的标志位**。

---

## 7. Error model

```csharp
class ApiError {
    string Kind;            // 见下表
    string Message;         // 面向开发者的英文说明
    int?   HttpStatus;
    bool   Retryable;       // 由 Kind 推导，别自己再定策略
    JObject Detail;         // 结构化补充，见下
    string Describe();      // 面向用户的中文一句话
    bool IsSessionProblem;  // Kind == session_expired
    bool IsSignatureProblem;
}
class DouyinApiException : Exception { ApiError Error; string Kind; bool Retryable; }
```

| Kind | 含义 | 该做什么 |
|---|---|---|
| `network` | 网络不可用/超时 | 可重试；显示重试按钮 |
| `http_error` | 服务器错误状态 | `Retryable` 视情况 |
| `empty_body` | **200 + 空 body**，请求被静默丢弃 | 不可重试；通常是签名/门禁问题，报出来 |
| `signature_invalid` | 签名被拒 | 不可重试；诊断 |
| `risk_control` | 风控/Argus 门 | 不可重试；`Detail["missingCredential"]` 会说是哪个凭据 |
| `session_expired` | 登录态失效 | 引导重新登录（Cookie 导入） |
| `schema_changed` | 接口结构变了 | 不可重试；需要更新解析层 |
| `not_found` | 内容已删/不存在 | 不可重试；返回上一页 |
| `rate_limited` | 被限流 | 退避后重试 |
| `unsupported_codec` | 没有设备可解码的源 | 不可重试；提示视频不兼容 |
| `signer_unavailable` | 功能在本次构建不可用（如扫码） | 看 `Detail["alternative"]` |
| `invalid_input` | 输入不合法（含重复提交） | 看 `Detail["reason"]` |
| `internal` | 客户端 bug | 报出来，带 traceback（在 `Detail`） |

**最重要的两种**（UI 一定要分开显示，否则用户会往错方向排查）：

- `Detail["gate"] == "argus"` + `Detail["missingCredential"] == "uifid"`
  → 服务端的安全门要求 `uifid`。这不是网络问题，也不是签名问题。
- `Kind == "empty_body"`
  → 200 但空 body，说明请求没到 handler。

---

## 8. 页面生命周期注意事项

| 时机 | 必须做 |
|---|---|
| `App.OnLaunched` | 构造 `ServiceHost`，`InitializeAsync(bootstrapGuestSession: true, ct)` |
| `App.Suspending` | `await host.ShutdownAsync(ct)`（停播放、flush 历史与 session） |
| `App.Resuming` | 重新 `host.Api.CheckSessionAsync(ct)` 校验会话；失败则提示重新导入 cookie |
| 页面 `OnNavigatedFrom` / 卸载 | `await host.ReleasePageResourcesAsync()` ← **内存验收项** |
| 页面 `OnNavigatedTo` | 建新的 `CancellationTokenSource`，离开时 `Cancel()` |
| 任何 UI 事件 | 只调 `ServiceHost` 的方法；`try/catch (DouyinApiException)` 后 `error.Describe()` 显示 |

### 线程规则（违反会静默出错）

- **CPython 引擎线程**：`PyUwpRuntime_*` 只能在**建它的那个线程**上调用。
  `EngineThread` 已经封装好，`PythonProtocolClient` 通过 `IEngineExecutor` marshal。
  **绝不要从 UI 线程直接 P/Invoke。**
- **`MediaElement`**：只能在 UI 线程操作。`MediaElementHost` 已经 marshal。
- 两个方向都不要**同步等待**对方，否则死锁。

---

## 9. 禁止 UI Agent 修改的代码

以下都**已验证通过**（Python 187 项、C# 237 项断言）。改它们会让测试失去意义：

```
src/DesultorySignals.Core/**        ← 核心逻辑全部
python/douyin/**                    ← 协议层全部
native/pythonhost/**                ← C ABI + 宿主冒烟
tests/**  python/tests/**           ← 测试
scripts/**                          ← 构建/测试脚本
_research/**                        ← 逆向证据
```

**可以改**（这些就是为你准备的）：

```
app/DesultorySignals/Views/*.xaml          ← 随便重做
app/DesultorySignals/Views/*.xaml.cs       ← CodeBehind
app/DesultorySignals/App.xaml(.cs)         ← 生命周期
app/DesultorySignals/Platform/**           ← 平台适配实现（但必须保持接口契约）
app/DesultorySignals/Assets/**             ← 图标
app/DesultorySignals/Package.appxmanifest  ← 显示名/图标；MinVersion 保持 10586
```

如果确实需要 Core 加一个方法：**加，不要改现有签名**。现有签名都有测试钉住。

### 真机排障用的两个文件开关（在 `App.xaml.cs`，别删）

| 文件（放在 LocalState） | 作用 |
|---|---|
| `disable-python.txt` | 跳过全部 Python 引导。用于把"XAML 壳坏了"和"CPython 桥接坏了"分开 |
| `startup-report.txt` | 启动时**自动写出**的 JSON 报告，含 `ttwid`/`uifid` 是否存在、自检结果、Feed 探测结果、脱敏日志全文 |

两者都是**读文件**而不是编译期常量，所以一次构建就能做 A/B，不用重编重签重装。
`deploy-device.ps1 -PullDiagnostics` 会把报告取回仓库根目录。

---

## 10. 10586 平台约束（会咬人的）

| 约束 | 说明 |
|---|---|
| `TargetPlatformMinVersion` | **必须 10.0.10586.0**。不要用 14393+ 才有的 API |
| 语言版本 | `LangVersion 7.3`。**不要**用 `switch` 表达式、`using var`、可空引用类型、元组解构 |
| netstandard1.4 | 没有 `System.Text.Json`、`System.Net.Http`、`IAsyncEnumerable`、`ValueTuple`、`Span` |
| XAML 编译器 | C++/CX 的 XAML 编译器在这台机器上是坏的；**C# 走托管编译器，正常** |
| `UseDotNetNativeToolchain` | Debug 保持 `false`（真机调试优先）。改 `true` 前先确认 rd.xml 覆盖了 Newtonsoft |
| appx 布局 | `Lib\`（Python 标准库）与 `AppLib\`（本项目的 douyin 包）必须在包根 —— native 驱动把它们加进 `sys.path` |
| 部署依赖 | 真机需要 `Microsoft.NET.CoreRuntime.1.1 (ARM)`（MinVersion 10.0.10049 → 10586 兼容），`AppPackages\...\Dependencies\ARM\` 里已带 |
| appx 签名 | 用自签开发证书 `DesultorySignals_TemporaryKey.pfx`（密码 `desultory`），由 `scripts\build-app.ps1` 用 signtool 签。**设备上要先信任同名 `.cer` 一次**（`Add-AppDevPackage.ps1` 会做） |

### 两条容易踩的坑（都已处理，别改回去）

**1. 传输层不许有自己的 cookie jar。**
`UwpHttpTransport` 必须设 `CookieUsageBehavior = NoCookies`。默认行为下 WinRT 客户端会自己
存 cookie 并在后续请求里自动附带，而我们也显式发 `Cookie` 头 —— 两套 jar 会让一个请求同时
带上会话的 cookie 和一份同名陈旧副本，这正是 brief 禁止的"cookie 属于身份 A 却按身份 B 签名"，
而且表现为**无迹可寻的间歇性 403**。设成 NoCookies 后 Python 侧的 jar 是唯一真相来源。

**2. 标准库被裁剪过，不要"顺手补回来"。**
`DesultorySignals.csproj` 里 `Lib` 的 `Exclude` 列表去掉了 41 MB 的死重量（主要是 `Lib/test`
35 MB / 1779 文件），把 appx 从 18.6 MB 降到 12.7 MB。排除的每一个目录都论证过
（见 csproj 注释），并且**实测**协议层的导入闭包完全不碰它们：
`base64 binascii bisect hashlib hmac ipaddress math random struct urllib.parse warnings`。
`encodings`、`importlib`、`json`、`urllib` 全部保留。

裁剪经过一次显式审计（`docs/DEFERRED.md` §B5）：闭包与 3.15 的 `Lib` 树逐项比对，
79 个必需 `.py` 全在。**注意一个坑**：`string` 在 3.15 里是**包**
（`Lib/string/__init__.py`），不是 `string.py`；按文件名找会误判为缺失。

---

## 11. 未解决问题（第二阶段不需要解决，但要知道）

1. **应用在真机上启动即闪退（当前阻塞项）**。装上 `Microsoft.NET.CoreRuntime.1.1` 后曾成功启动并
   存活（28.6 MB 工作集），之后启动开始返回 `HTTP 500 0x8027025B`。
   排障手段见 `docs/DEVICE-RUNBOOK.md` §3：
   在 LocalState 放一个 `disable-python.txt` 就能让应用跳过全部 Python 引导，
   从而判定问题在 XAML 壳还是在 CPython 桥接（原生崩溃无法被托管代码捕获，所以这个开关是必要的）。
   注意设备上已装的**原生**应用 `EdgeHTMLReborn.Harness` 也启动即退，设备上还有 6 个它的崩溃转储——
   所以要先排除设备层面的原因。
2. **真机尚未取得 `ttwid`**。`startup-report.txt` 里有 `identity.ttwid` 字段专门回答这个问题，
   但应用还没活到写报告那一步。**`ttwid` 是唯一的真门禁**：服务端不下发它之前，所有 API 调用都会被拒。
3. **`uifid` 获取链路缺失** —— 当前 detail 与评论被 Argus 门拦下（403 / 200 空 body）。
   Feed 正常。需要补的是：从 webmssdk 或通过 `__ac_signature` 验证码页拿到 `uifid`。
   详见 `docs/ARCHITECTURE.md` 的"已知风控限制"与 `_research/abogus/EVIDENCE.md`。
4. **`a_bogus` 逆向未完成** —— 编码器半部分已验证（6 个真实签名逐字节复现），
   payload 半部分未解（`_abogus_research.build_current_payload` 会抛
   `IncompleteReverseError` 并列出未解偏移）。**当前不需要它**，因为门是 `ttwid`。
5. **10586 完全未验证** —— 真机是 **15254.603**。appx 的 `MinVersion=10.0.10586.0` 向前兼容所以能装能跑，
   但 10586 上的行为（尤其是 AppContainer 下的路径与文件写入）仍是未知。
6. **扫码登录不可用** —— 见 §6。
7. **`_sqlite3` 不存在** —— 用文件型 store（`IFileStore`）作为等价方案。

### 真机已验证的部分

- appx 部署与注册（`PackageOrigin=5`，即侧载）
- 应用可被激活并运行（曾存活 8 秒以上，工作集 28.6 MB）
- appx 布局正确：包根 10 KB 原生 stub + `entrypoint\` 316 KB 托管 IL（app-local-CLR 的 Debug 布局）
- 设备强制要求 appx 签名（未签名 → `0x800B0100`）
- 框架依赖必须**独立安装**，且**不会**被 WDP 的包列表枚举

### 真机尚未验证的部分

- CPython 3.15 在 ARM32 AppContainer 里能否初始化（本机 x64 已验证，见 `DEFERRED.md` §B5b）
- 协议层能否在嵌入式解释器里导入并自检通过
- `ttwid` 能否到手（**这是决定后续一切的关键**）
- 传输层能否收到 `Set-Cookie`
- `MediaElement` 解码与 `IRandomAccessStream` 适配
- 连续刷视频的内存曲线

---

## 12. 构建与验证命令

```powershell
# ARM32 CPython 静态核（一次，几分钟）
pwsh -NoProfile -File scripts\build-pythoncore.ps1

# 扩展宿主 ABI → native\out\python-uwp.dll
pwsh -NoProfile -File scripts\build-pythonhost.ps1

# 全部测试（三套，483 项，都不需要真机）
pwsh -NoProfile -File scripts\test-all.ps1

# 单独跑：
pwsh -NoProfile -File scripts\test-python.ps1              # 协议层逻辑（桌面 3.13）187 项
pwsh -NoProfile -File scripts\test-python.ps1 -Online      # 额外打真实 API 的 gate 检查
pwsh -NoProfile -File scripts\test-core.ps1                # C# 核心 237 项
pwsh -NoProfile -File scripts\test-pythonhost-desktop.ps1  # 嵌入式 CPython 3.15 + 宿主 ABI 59 项

# appx（ARM, MinVersion 10586），构建后自动签名
pwsh -NoProfile -File scripts\build-app.ps1 -Configuration Debug

# 部署到真机（自动先装框架依赖 + 签名校验 + 启动 + 取回启动报告）
pwsh -NoProfile -File scripts\deploy-device.ps1 -DeviceIp 192.168.3.159 -Launch -PullDiagnostics
```

**`test-pythonhost-desktop.ps1` 是改 Core 或协议层后最该跑的那一套。** 它在**真实的嵌入式
CPython 3.15** 里跑协议层（不是桌面 3.13 解释器），所以能抓到只在打包后才暴露的问题。
它用与 appx 完全相同的标准库裁剪，所以也验证了发布布局。

**部署与真机排障请看 [docs/DEVICE-RUNBOOK.md](docs/DEVICE-RUNBOOK.md)**：
六个 WDP 的坑、崩溃转储分析、`disable-python.txt` 诊断开关、以及设备反复掉线的重试写法。
首次部署后必须先确认 `startup-report.txt` 里的 `identity.ttwid` 是 `present`，
否则后面所有 API 调用都会失败而原因看起来像签名问题。

真机验证的下一步：把 appx 与 `.cer` 一起装到设备，先信任证书，启动后看诊断页。
**首先要确认的是 `ttwid` 是否拿到了** —— 拿不到的话每个 API 调用都会被拒，
而 `DiagLog` 会直接写出这一点（`SessionManager` 会 Warn "no ttwid was obtained"）。
