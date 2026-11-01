# 实际使用的 API Surface

本文件列出第一阶段**真正调用**的每一个外部接口，以及证据强度。

证据强度标记：

- **[实测]** —— 2026-10-07 对真实服务发出请求并观测到响应（`python/tests/online_gate.py` 或 `_research/abogus/EVIDENCE.md`）。
- **[社区]** —— 来自公开 downloader 生态的路径形状，未在本机复现。
- **[候选]** —— 存在但未验证；有 fallback 变体，且失败会被分类而不是崩溃。

---

## 1. 传输层

| 项 | 值 |
|---|---|
| 实现 | 真机 `Windows.Web.Http.HttpClient`（`UwpHttpTransport`）；桌面 `urllib.request`（测试） |
| 方法 | `GET` 读，`POST` 写（form-urlencoded） |
| TLS | **由 native 承担**。CPython 3.15 静态核没有 `_ssl`，Python 侧不碰 socket |
| 代理 | 不特殊处理；真机走系统网络栈 |
| 超时 | 请求级 `HttpRequestSpec.TimeoutMs`（默认 20 s；整文件下载 120 s；range 取块用重试策略的短预算） |
| 重定向 | 默认跟随；**range 请求强制不跟随**（重定向后的 range 读取可能返回错误偏移的字节） |
| Cookie | 由 Python 组装成单个 `Cookie` 头显式发送，不用 CookieContainer |

### 请求头（每个请求，顺序固定）

```
User-Agent            虚拟浏览器身份的 UA（签名必须用同一个）
Accept                application/json, text/plain, */*
Accept-Language       <identity.language>,zh;q=0.9,en;q=0.8
Referer               https://www.douyin.com/
Origin                https://www.douyin.com
Sec-Fetch-Dest        empty
Sec-Fetch-Mode        cors
Sec-Fetch-Site        same-origin
Cookie                由 CookieJar + 身份指纹参数合成（见下）
Content-Type          POST 时 application/x-www-form-urlencoded; charset=UTF-8
Range                仅 range 读取时，bytes=start-end（闭区间）
```

`Accept-Encoding` 被刻意设为 `identity`：客户端不做解压，`Windows.Web.Http` 自己处理。

---

## 2. 端点

### 2.1 Feed（推荐流）—— **[实测] 200 + 234 KB 真实数据**

```
GET https://www.douyin.com/aweme/v1/web/tab/feed/
```

| 参数 | 值 |
|---|---|
| `count` | 页大小（客户端发 6~20） |
| `refresh_index` | 刷新计数，每次 Refresh 递增 |
| `refer_type` | `feed` |
| `refer_share_id` / `refer_share_uid` / `refer_share_channel` / `refer_share_form` | 空 |
| `volume` | `0` |
| 设备块 | 见 §2.6 |
| `a_bogus` | **不发**（策略 `omit`） |

响应：`{"status_code":0,"aweme_list":[...],"has_more":1,...}`
归一化为 `FeedPage { Items, Cursor, HasMore, Skipped, SourceKey }`。

变体回退：`/aweme/v1/web/module/feed/`（[候选]）。

### 2.2 视频详情 —— **[实测] 403，Argus 门要求 uifid**

```
GET https://www.douyin.com/aweme/v1/web/aweme/detail/
```

| 参数 | 值 |
|---|---|
| `aweme_id` | 目标 id |
| `verifyFp` / `fp` | 身份的 `verifyFp` |
| 设备块 | 见 §2.6 |
| `a_bogus` | **不发**（策略 `omit`；加了会从 200 变 403） |

响应（成功时）：`{"status_code":0,"aweme_detail":{...}}`。
失败时可能是 `{"aweme_detail":null,"filter_detail":{...}}` → 归一化为 `not_found` / `risk_control`。

### 2.3 评论列表 —— **[实测] 200 + 空 body（同一门禁）**

```
GET https://www.douyin.com/aweme/v1/web/comment/list/
```

| 参数 | 值 |
|---|---|
| `aweme_id` | 目标 id |
| `cursor` | 起始 `0`，之后用响应的 `cursor` 原样回传 |
| `count` | 页大小 |
| `item_type` | `0` |
| `a_bogus` | **必须存在且非空**（策略 `presence`，值不被校验） |

响应：`{"status_code":0,"comments":[...],"cursor":20,"has_more":1,"total":733}`
（`total:733` 与 20 条评论是实测观测值）。

**`status_code:0` 且没有 `comments` 键 = 该视频没有评论，是成功不是漂移。**

### 2.4 二级回复 —— **[社区]**

```
GET https://www.douyin.com/aweme/v1/web/comment/list/reply/
```

参数：`item_id`（= aweme id）、`comment_id`（父评论）、`cursor`、`count`、`item_type`、`a_bogus`（presence）。

### 2.5 发布评论 —— **[社区]，只做只读探测**

```
POST https://www.douyin.com/aweme/v1/web/comment/publish/
Content-Type: application/x-www-form-urlencoded; charset=UTF-8
```

body 参数（顺序固定，签名覆盖这个顺序）：

```
aweme_id=<id>&text=<urlencoded>&text_extra=[]&reply_id=0&reply_to_reply_id=0
```

- 一级评论：两个 reply id 都是字面量 `"0"`。
- 回复：`reply_id` = 被回复的评论 id。
- 嵌套回复：`reply_id` + `reply_to_reply_id` 都要给（客户端本地校验：只给后者会被拒）。
- `text_extra` 是 at 提及 payload，作为 **JSON 字符串**嵌在 form body 里。
- `a_bogus` 策略 `presence`（未实测写路径，取保守值）。

**未对真实账号做写操作。** 响应形状为社区观测：`{"comment":{...}}` 或 `{"comment":null,"status_code":<非0>,"status_msg":"..."}`。
后者按 `status_msg` 判定是否为审核拒绝（`risk_control`，不可重试）。

### 2.6 设备块（每个 API 请求都带，顺序即签名顺序）

```
device_platform=webapp  aid=6383  channel=channel_pc_web  pc_client_type=1
pc_libra_divert=Windows  version_code=190500  version_name=19.5.0
cookie_enabled=true  screen_width=1920  screen_height=1080
browser_language=zh-CN  browser_platform=Win32  browser_name=Chrome
browser_version=138.0.0.0  browser_online=true  engine_name=Blink
engine_version=138.0.0.0  os_name=Windows  os_version=10.0
cpu_core_num=8  device_memory=8  platform=PC
downlink=10  effective_type=4g  round_trip_time=50
webid=<19 位>  uifid=<有则带>  msToken=<有则带>
```

`screen_width/height`、`browser_*`、`os_*`、`cpu_core_num` 等**全部来自虚拟身份**，
不是设备的真实值。

### 2.7 游客会话引导 —— **[实测] 200**

三个步骤，因为单独任何一个都不稳：

| 步 | 请求 | 结果 |
|---|---|---|
| 1 | `GET https://www.douyin.com/` | 本机返回 72914 字节 JSVM 引导页 + `Set-Cookie: __ac_nonce=...`（**不下发 ttwid**） |
| 2 | `POST https://ttwid.bytedance.com/ttwid/union/register/` | 200 + `Set-Cookie: ttwid=1%7C...%7C<exp>%7C<hex>; Domain=bytedance.com` + body 里的 `redirect_url` |
| 3 | `GET <redirect_url>`（douyin.com 域） | 200；实测**清掉了 Argus 对 detail 的拦截** |

步 2 的 body：

```json
{"region":"cn","aid":1768,"needFid":false,"service":"www.douyin.com",
 "migrate_info":{"ticket":"","source":"node"},"cbUrlProtocol":"https","union":true}
```

`service` 实测取值不影响下发（试过 `www.douyin.com` / `douyin.com` / `aweme` / `www.ixigua.com`），
但**下发的 Domain 恒为 `bytedance.com`**。客户端因此显式发送 `Cookie` 头，不依赖域匹配。

### 2.8 分享链接解析

| 形态 | 处理 |
|---|---|
| `v.douyin.com/XXXX/` | 跟随重定向，从**最终 URL** 重新解析 id（不抓 HTML） |
| `www.douyin.com/video/<id>` `/note/<id>` | 直接取路径里的 id |
| `www.douyin.com/discover?modal_id=<id>` | 取 query |
| `www.iesdouyin.com/share/video/<id>/` | 取路径 |
| 纯 `<15-20 位数字>` | 直接当 id |
| 任意文本里嵌的上述链接 | 先抽 URL 再按上表 |

### 2.9 登录 —— 见下节

---

## 3. 登录方式

### 3.1 Cookie 导入（**唯一可用路径**）

不调用任何端点。接受三种粘贴格式：

1. 原始 Cookie 头：`sessionid=...; ttwid=...`
2. JSON 数组：`[{"name":"sessionid","value":"...","domain":".douyin.com"}]`
3. Netscape `cookies.txt`（制表符分隔）

导入时把指纹类 cookie（`ttwid`、`s_v_web_id`）**镜像到身份字段**，
保证签名/指纹/cookie jar 三者一致。

判定登录的 cookie：`sessionid` / `sessionid_ss` / `sid_tt` / `sid_guard` 任一存在。

### 3.2 扫码登录 —— **[实测] 已不可用**

```
GET https://sso.douyin.com/get_qrcode/      → 200，9938 字节 JS 应用页（不是 QR payload）
GET https://sso.douyin.com/check_qrconnect/ → [候选]，无法驱动
```

客户端对该操作直接返回 `signer_unavailable`，并在 `Detail["alternative"]` 里指向
`login.importCookie`。状态机与解析器保留，`login.QR_FLOW_AVAILABLE = False` 是唯一开关。

### 3.3 会话有效性探测 —— **[候选]**

```
GET https://www.douyin.com/aweme/v1/web/user/profile/self/     [候选]
GET https://www.douyin.com/passport/account/info/              [候选]
```

本地判定（`login.assess`）不依赖端点：直接读 cookie jar，所以"以为登录了其实没有"不可能发生。

---

## 4. 签名

| 项 | 状态 |
|---|---|
| 真实门禁 | **服务端下发的 `ttwid` cookie**（[实测]） |
| `a_bogus` | 编码器半部分已验证（6 个真实签名逐字节复现）；payload 半部分未解 |
| 策略 | `omit`（detail/feed/user）、`presence`（comment_list/replies/publish）、`required`（当前无） |
| 编码约定 | 参数顺序即签名顺序；`encodeURIComponent` 语义（`+`→`%2B`，`!` 保留） |

签名 provider 注册表：`none` / `presence` / （完成后的）`a_bogus`。
`POLICY_REQUIRED` 的操作在只有占位 provider 时抛 `signer_unavailable`，
**绝不**用占位值冒充真签名。

---

## 5. 存储

无 `_sqlite3`，用文件型 store（`IFileStore`）。

| 路径 | 内容 | 上限 |
|---|---|---|
| `session/identity.json` | 虚拟身份 + cookie jar | 单文件，写前比较，未变不写 |
| `session/publish-guard.json` | 重复发布守卫 | 单文件 |
| `metadata/aweme/<id>.json` | 每个 aweme 一个文件 | 400 条，6 小时过期 |
| `image/index.json` + `image/<hash>.bin` | 图片 LRU（键 = URL 去 query 后哈希） | 500 条 / 48 MiB |
| `history/history.json` | 观看历史（顺序即数据） | 200 条，写入去抖 5 s |
| `media/<id>.mp4` | 下载式播放的落盘 | 由用户/清理策略管 |
| `site-packages/` | CPython 本地包管理器的目标目录 | — |

路径经 `StorePath.Normalize` 校验，`..` 与绝对路径会被拒绝。

---

## 6. CPython 3.15 运行时

| 项 | 值 |
|---|---|
| 运行时根 | `Package.Current.InstalledLocation.Path` |
| `sys.path` | `<root>\Lib`（标准库）→ `<root>\AppLib`（本项目的 `douyin` 包）→ LocalState `site-packages` |
| 线程 | **单引擎线程**；ABI 调用必须来自建它的线程，否则返回 `-2` |
| 配置 | `PyConfig_InitIsolatedConfig`，`site_import=0`，`write_bytecode=0`，`use_environment=0` |

### 可用 vs 不可用（这决定了所有设计）

**可用**（静态核内置，`CMakeLists.txt` 源清单为证）：
`_socket`、`select`、`zlib`、`binascii`、`_json`、`_struct`、`_random`、`_pickle`、
`md5`、`sha1`、`sha2`、`sha3`、`blake2`、`hmac`、`_csv`、`_datetime`、`_sre`、
`math`、`cmath`、`_io`、`_thread`、`_weakref`、`_collections`、`_functools`、
`_statistics`、`_tracemalloc`、`_opcode`、`_typing`、`_suggestions`、`cjkcodecs`

**不可用**（**必须知道**）：
`_ssl` / `ssl` —— 所以 TLS 只能在 native 层。
`_sqlite3` —— 所以用文件型 store。
`_hashlib`（OpenSSL）—— 但 `hashlib` 由 `_md5`/`_sha1`/`_sha2`/`_sha3`/`_blake2` 后端支撑，**可用**。
`_lzma` / `_bz2` / `_decimal` / `_ctypes` / `_uuid` —— 未编入，且当前不需要。

### 宿主 ABI（`native/pythonhost/PyUwpDriver.h`）

```c
int  PyUwpRuntime_Initialize(const wchar_t *runtime_root, const wchar_t *local_site);
int  PyUwpRuntime_CallJsonEx(const char *request_utf8,
                             const char **response_utf8, int *response_length);
int  PyUwpRuntime_CallJson(const char *request_utf8, const char **response_utf8);
const char *PyUwpRuntime_LastResponse(void);
int  PyUwpRuntime_BridgeReady(void);
int  PyUwpRuntime_RunUtf8(const char *source);
int  PyUwpRuntime_EvalInt(const char *expression, long *value);
int  PyUwpRuntime_Finalize(void);
const char *PyUwpRuntime_LastError(void);
const char *PyUwpRuntime_LastOutput(void);
```

返回码：`0` OK，`-1` 参数错，`-2` 线程错，`-1000` 初始化失败，
`-1100` 桥接不可用，`-2000` 执行失败，`-2100` 响应超限（16 MiB）。

`CallJsonEx` 是相对上游 AlterGate ABI 的**唯一新增**：上游只能执行源码并从 64 KiB 静态
stdout 缓冲读回，不是可用通道。响应缓冲由运行时拥有、按需增长、下次调用前有效。

---

## 7. 桥接协议（C# ↔ Python）

单一入口 `douyin.protocol.handle_json(request) -> response`，**永不抛异常**。

```jsonc
// 请求
{ "op": "request.build", "clock": {"seed": 1, "fixedNowMs": 1700000000000}, ... }

// 响应
{ "ok": true,  "op": "...", "result": { ... } }
{ "ok": false, "op": "...", "error": { "kind": "...", "message": "...",
                                       "httpStatus": null|int, "retryable": bool,
                                       "detail": { ... } } }
```

`clock` 是可选注入：生产不发（用真实挂钟与真随机），测试发固定 seed 与时间戳 → 签名可复现。

### 操作清单

| op | 作用 |
|---|---|
| `identity.create` / `identity.load` / `identity.reset` | 身份生命周期 |
| `identity.ensureFresh` | 只轮换设计上会过期的子字段（`msToken` 10 min，`verifyFp` 14 d） |
| `identity.applyCookies` | 吸收 `Set-Cookie`，镜像指纹字段 |
| `identity.redacted` | 日志安全的身份视图 |
| `request.build` | 构造签名请求（**每次调用重新签名**） |
| `request.buildPlain` | 无签名无设备块的请求（重定向解析、页面引导） |
| `response.classify` | 分类 + 吸收 cookie（同一步，无法忘记） |
| `response.awemeDetail` / `awemePage` / `commentPage` / `user` | Schema 归一化 |
| `share.parse` / `share.parseRedirect` / `share.requireAwemeId` | 分享链接 |
| `login.qrCreate` / `qrApplyCreate` / `qrCheckPlan` / `qrCheckApply` | 扫码（当前返回不可用） |
| `login.importCookie` | **主要登录路径** |
| `login.assess` / `login.logout` / `login.require` | 会话 |
| `comment.buildPublish` / `parsePublish` / `guard` / `guardCommit` | 发布 + 重复守卫 |
| `diagnostics.catalogue` | 端点目录 + 签名策略 + 签名能力状态 |
| `diagnostics.selfTest` | 进程内自检（设备首启写结果文件） |

---

## 8. 平台能力依赖（真机部署需要）

| 项 | 值 |
|---|---|
| 目标 | Windows 10 Mobile **10586** / ARM32 / UWP App Container |
| 框架依赖 | `Microsoft.NET.CoreRuntime.1.1`（MinVersion 10.0.10049 → 10586 兼容）；appx 已捆绑 ARM 包 |
| 权限 | `internetClient`、`privateNetworkClientServer` |
| `MediaElement` | 用于播放（第一阶段调试壳直接用它） |
| `Windows.Web.Http` | TLS 传输 |
| `Windows.Storage.Streams.IRandomAccessStream` | 把 `IRandomAccessByteSource` 接到 `MediaElement` |

**不使用**：WebView / WebKit（brief 明令）、WinUI、14393+ 专有 API、外部服务器。
