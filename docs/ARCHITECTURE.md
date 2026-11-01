# 架构

目标平台：**Windows 10 Mobile Build 10586 (Version 1511) / ARM32 / UWP App Container**。
本阶段只做核心层，不做正式 UI。

## 一句话

C# 负责生命周期、传输、存储、播放器；Python 负责协议、签名、身份、Schema；
两者之间只有一个函数和一种 JSON 信封。

```
                    ┌──────────────────────────────────────────┐
   调试壳（C# UWP）  │ Views/MainPage · Views/DiagnosticsPage   │  只做生命周期/事件/状态
                    │ CodeBehind：调 Service，不放业务逻辑      │
                    └───────────────────┬──────────────────────┘
                                        │  ServiceHost（唯一组合根）
                    ┌───────────────────▼──────────────────────┐
   核心逻辑（C#）    │ Services：Feed Video Comment Playback     │  netstandard1.4，桌面可单测
                    │           Cache History                   │
                    │ Douyin：  DouyinApiClient SessionManager  │  ← 唯一知道 endpoint 的层
                    │ Media：   RangeMath RangeCache            │
                    │           VideoSourceSelector             │
                    │           RemoteRandomAccessByteSource    │  ← 真正的随机访问
                    │ Networking：IHttpTransport RetryPolicy    │
                    │ Diagnostics：DiagLog Redactor             │
                    └───────────────────┬──────────────────────┘
                                        │  PyUwpRuntime_CallJsonEx（一个函数）
                    ┌───────────────────▼──────────────────────┐
   协议层（Python）  │ douyin.protocol.handle_json               │  仅标准库，无 socket 无 ssl
                    │ identity cookies signer endpoints         │  无文件系统，无时间/随机直读
                    │ request_builder response_adapter login    │  时钟可注入 → 可复现
                    │ comment share                            │
                    └───────────────────┬──────────────────────┘
                                        │  C ABI
                    ┌───────────────────▼──────────────────────┐
   CPython 3.15     │ python-uwp.dll（AlterGate 移植的静态核）   │  ARM32 / AppContainer
                    └──────────────────────────────────────────┘
```

## 为什么这样切

### 1. 传输不绑 Python

嵌入式 CPython 3.15 静态核**没有 `_ssl`，也没有 `_sqlite3`**（`CMakeLists.txt` 的源清单里
确实没有 `_ssl.c`；`Lib` 里也没有 `ssl.py`）。所以 TLS 必须由 native 承担。这不是妥协，
是唯一可行路径，而且正好满足 brief 第 7 条。

于是 `IHttpTransport` 成为核心层唯一的网络依赖：

| 实现 | 场景 |
|---|---|
| `UwpHttpTransport`（`Windows.Web.Http`） | 真机 |
| `FakeTransport` | 桌面单测，可脚本化"先 403 后 206" |

Python 只产出 `{url, method, headers, body, cookie}`，收到 `{status, headers, body}`。
**Python 侧永远不碰 socket。**

### 2. 协议层是纯函数

`douyin.protocol.handle_json(request) -> response` 是唯一入口，且**永不抛异常**。

- 不读文件系统：cookie jar、身份、session 全部以 JSON 传入传出，存储由 C# 拥有。
- 不读挂钟、不直接调 `random`：一切经 `Clock`（可注入 seed 与固定时间戳）。
- 因此整个协议层可以在桌面解释器上跑完整单测，签名可复现。

这条约束的直接收益：`python/tests/run_tests.py` 有 187 个断言，全部不需要真机。

### 3. 签名策略是数据，不是代码

这是本项目最重要的经验结果，也是**最容易做错**的地方。2026-10-07 实测
（`_research/abogus/EVIDENCE.md`）：

| 操作 | 实测结论 |
|---|---|
| `aweme/detail` + `ttwid`，**不带** `a_bogus` | **200 + 67 KiB JSON** |
| 同一请求**带** `a_bogus`（含用官方 SDK 现算的真实值） | **403** |
| `comment/list` + `ttwid` + `&a_bogus=AA` | **200 + 评论列表** |
| `comment/list` + `ttwid`，**不带** `a_bogus` | 200 + **空 body** |
| `tab/feed` + `ttwid`，不带 `a_bogus` | **200 + aweme_list** |

结论：**真正的门是服务端下发的 `ttwid` cookie，不是合法签名；而且在不需要签名的
地方加签名会让请求从 200 变成 403。**

所以策略写在 `endpoints.SIGNATURE_POLICY` 表里，三种取值：

- `POLICY_OMIT` —— 一个字节都不加（detail / feed / user）。加了会坏。
- `POLICY_PRESENCE` —— 加一个非空占位值（comment/list）。该端点只检查**存在性**。
- `POLICY_REQUIRED` —— 需要真签名。当前**没有任何操作**使用它，留着是为了将来风控收紧时
  只需改表 + 注册一个 provider，而不是重构。

把这张表弄反的后果，就是一个在 feed 上正常、在 detail 上莫名 403 的客户端。

### 4. 错误分类是产品特性

brief 要求"必须能区分错误原因"。`response_adapter.classify()` 的判定顺序就是为此设计的：

1. `429` 先判 —— 它用纯文本 body，否则会被误读成签名问题，而两者的补救完全相反。
2. **Argus 门单独识别**：body 里出现 `ArgusSecurityPlugin` 时，解析出它索要的凭据名
   （`uifid` / `ttwid` / `msToken`），报成 `risk_control` 并带上 `detail.missingCredential`。
   实测的 body 是 `Blocked by ArgusSecurityPlugin Uifid Not Found` —— 比"403"有用得多。
3. **HTTP 200 + 空 body** 单独识别为 `empty_body`，并标为**不可重试**：
   这是"请求被静默丢弃"的特征，原样重发只会再得到一次空。
4. 非 JSON 的非空 body 在风控状态码上判为挑战页。
5. 之后才看 body 里的 `status_code`。

`ApiError.Retryable` 由 kind 推导，调用方不要自己再定策略。

### 5. 重试策略区分读与写

`RetryPolicy` 的关键不是"重试几次"，而是**写操作的"可判定"**：

- 读：任何可重试分类都重试，指数退避 + 抖动（抖动是必要的——feed/评论/图片可能同时失败，
  同步重试会重现导致失败的那波拥塞）。
- 写：**只在"请求根本没拿到响应"时重试一次**。5xx 或超时后请求可能已经生效，
  重发就是重复评论。这一点用工厂方法表达（`ForWrites()`），而不是一个调用方可能填错的布尔。
- 未分类异常（我们自己的 bug）不重试。

写路径还有第二道闸：`PublishGuard`，按 `(aweme, text, reply 目标)` 指纹在 60 秒窗口内
拒绝重复提交，且**只在确认发布成功后**才推进。它覆盖网络层看不见的情况：用户连点两次。

### 6. 播放：先证明链路，再优化

brief 的顺序是对的，两条路径都在 `PlaybackService` 里且共用除字节投递外的一切：

- `PlayDownloadedAsync` —— 整文件下载到 `StorageFile` 再播。**保留它**：它隔离变量。
  这条通、流式不通 → 问题在 range；两条都不通 → 问题在解码器/容器/字节。
- `PlayStreamingAsync` —— `RemoteRandomAccessByteSource` 按需取 range。

`RemoteRandomAccessByteSource` 处理四个真实危险：

| 危险 | 处理 |
|---|---|
| 播放器会 seek（先读尾部 moov atom） | 按 chunk 惰性取，seek 只花一次请求 |
| URL 会过期（签名短时有效） | 403/404/410 → 回调 `VideoService.RefreshSources` 换新 URL、**丢弃整个缓存**、重试 |
| CDN 忽略 Range 返回 200 全量 | 从全量 body 里切出请求窗口，并用它修正真实大小 |
| 连接卡住 | 每次取 chunk 有独立短超时与独立重试预算 |

**不假设顺序读**，这是 `IRandomAccessByteSource` 的契约。

### 7. 内存有界

完成标准要求"连续刷视频不持续增长内存"。界分布在三个所有者：

| 所有者 | 机制 |
|---|---|
| `RangeCache` | 按 chunk 数上限 LRU（默认 48 × 512 KiB = 24 MiB），`InvalidateAll`/`InvalidateFrom` |
| `DiagLog` | 环形缓冲（默认 2048 条），丢弃计数可见 |
| `CacheService` | 图片按条目数 + 字节双上限 LRU；元数据按条目数 |
| `FeedService` | 去重集合超过 4000 时按列表尾部重建 |
| `CommentService` | `Clear()`，由 `ServiceHost.ReleasePageResourcesAsync()` 在页面离开时调用 |

页面离开/挂起时必须调 `ReleasePageResourcesAsync()` —— 这是那个验收项的落点。

### 8. 秘密不进日志

一个文件负责：`Diagnostics/Redactor.cs`。所有日志路径都经 `DiagLog.Write`，而它在**写入时**
scrub 消息与结构化数据。所以"忘了脱敏"不可能发生，而不是靠二十处自觉。

保留的是**形状**而不是值：`sessionid=<secret>`、`msToken=<redacted:107>`、cookie 名与条数。
理由是：诊断的真问题常常是"这个 cookie 到底在不在"，只写 `<redacted>` 会让日志失去用途。

`Redactor.Text()` 是兜底，捕捉从没预料到的路径（比如传输层异常消息）带进来的凭据。

## 已知风控限制（实测，可复现）

`python/tests/online_gate.py` 的结果：

| Gate | 结果 |
|---|---|
| 游客会话（ttwid） | **通过** —— 首页不下发时，用 `POST ttwid.bytedance.com/ttwid/union/register/` + 跟随其 `redirect_url` 回调拿到 |
| Phase 2：Feed | **通过** —— 200，234 KB，真实标题/点赞/评论数 |
| Phase 1：aweme detail | **失败** —— 403 `Blocked by ArgusSecurityPlugin Uifid Not Found` |
| Phase 5：评论列表 | **失败** —— 200 空 body |
| Phase 5：二级回复 | 未测（依赖评论列表） |
| Phase 3/4：可播放源 | 未测（依赖 detail） |

**根因**：Argus 门额外要求 `uifid`。它只能由 webmssdk 或"通过 `__ac_signature` 验证码页"的
首页下发；本机网络路径上首页返回的是 72914 字节的 JSVM 引导页并只设 `__ac_nonce`。
伪造 `uifid`/`odin_tt`/`msToken`/`s_v_web_id` 均已实测失败。

**这不是客户端 bug**：请求构造、参数顺序、编码、cookie 作用域、UA 都已被 Feed 的 200 证明是正确的。
需要的是补上 `uifid` 的获取链路（见 HANDOFF 的"未解决问题"）。

## 验证策略

目标设备是 ARM32 且不总可达，所以验证被分成四层，每层证明不同的东西：

| 层 | 命令 | 证明什么 | 项数 |
|---|---|---|---|
| 协议逻辑 | `test-python.ps1` | 参数顺序、编码、签名策略、Schema 归一化、状态机。跑在桌面 3.13 上 | 187 |
| C# 核心 | `test-core.ps1` | range 算术、缓存淘汰、编解码排序、重试策略、分页与去重、cookie 采纳 | 237 |
| **嵌入式运行时** | `test-pythonhost-desktop.ps1` | **真实 CPython 3.15 启动、扩展 ABI、协议层在嵌入式解释器内执行** | 59 |
| 真实网络 | `test-python.ps1 -Online` | 请求真的被服务端接受（或按预期被拒） | gate |

第三层是最容易被忽略、也最有价值的一层。它把**同一份** `desultory-driver.c` 编译到 x64，
链接上游的 x64 `pythoncore.lib`，用一个只依赖 `LoadLibrary`/`GetProcAddress` 的 C 宿主
驱动协议层。因为宿主是 C，所以"它能跑"就意味着 C# 的 P/Invoke 绑定不会因 ABI 原因失败。

它比第一层强在两点：

1. **解释器版本不同。** 第一层跑在桌面 3.13 上，打包用的是 3.15 的 `Lib` 树。
   两者不同，而差异只在打包后暴露（见 `DEFERRED.md` §B5 的 `string` 包化案例）。
2. **标准库被裁剪过。** 第三层用的是与 appx 完全相同的排除列表，所以它验证的是
   发布布局，不是一个理想化的完整标准库。

它还顺带做了一次**沙箱能力探测**，把架构决策从"推断"变成"实测"：

```
_ssl-no ssl-no _sqlite3-no sqlite3-no _socket+yes socket+yes zlib+yes
_hashlib-no hashlib+yes json+yes urllib.parse+yes select+yes ctypes-no lzma-no bz2-no
```

`_ssl` 不存在 → TLS 必须由 native 承担（§1）。`_sqlite3` 不存在 → 文件型 store。
`_hashlib` 不存在但 `hashlib` 可用 → 签名原语齐全，无需移植 OpenSSL。

第四层是唯一会因外部原因失败的层，所以它**不断言风控结果**，只记录原始 HTTP 状态与
响应前缀，让失败可归因。

## 目录

```
src/DesultorySignals.Core/     核心逻辑（netstandard1.4）
  Models/                      Aweme Comment Author VideoSource ... + JsonValue 读取器
  Diagnostics/                 DiagLog Redactor
  Python/                      IEngineExecutor IPythonRuntimeBackend IDouyinProtocol
  Networking/                  HttpRequestSpec HttpResponseData IHttpTransport RetryPolicy
  Storage/                     IFileStore StorePath MemoryFileStore
  Media/                       RangeMath RangeCache VideoSourceSelector
                               IRandomAccessByteSource RemoteRandomAccessByteSource
  Douyin/                      DouyinApiClient SessionManager
  Services/                    FeedService VideoService CommentService PlaybackService
                               CacheService HistoryService ServiceHost
python/douyin/                 协议层（仅标准库）
  protocol.py                  唯一入口 handle_json
  identity.py cookies.py       虚拟浏览器身份 + Cookie jar
  signer.py signer_abogus.py   签名 provider 注册表
  endpoints.py                 端点目录 + 签名策略（关键）
  request_builder.py           参数顺序/编码/签名/cookie
  response_adapter.py          分类 + Schema 归一化
  login.py comment.py share.py 登录状态机 / 发布 / 分享链接
  _abogus_research.py          逆向产物（参考，未接入）
app/DesultorySignals/          C# UWP 10586 调试壳
native/pythonhost/             扩展的宿主 ABI（源码）
tests/DesultorySignals.Tests/  C# 桌面测试运行器
python/tests/                  Python 测试 + online_gate.py
_research/abogus/              a_bogus 逆向证据与产物
scripts/                       构建脚本
```
