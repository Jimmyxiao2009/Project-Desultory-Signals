# 已知限制与后续工作

本阶段（核心层）交付时的诚实清单。每一项都写清：是什么、为什么、怎么补、影响哪些验收项。

---

## A. 阻塞性（挡住部分验收项）

### A1. `uifid` 缺失 → `aweme/detail` 与评论接口被 Argus 门拦下

**现象**（2026-10-07 实测，可复现）：

```
GET /aweme/v1/web/aweme/detail/?aweme_id=...   → 403, 46 bytes
     Blocked by ArgusSecurityPlugin Uifid Not Found
GET /aweme/v1/web/comment/list/?...&a_bogus=AA → 200, 0 bytes（空 body）
```

**根因**：服务端安全门（Argus）要求 `uifid`。它只能由 webmssdk 计算，或由
"通过 `__ac_signature` 验证码校验"的首页下发。本机网络路径上：

- `GET https://www.douyin.com/` → 200，72914 字节 JSVM 引导页，只设 `__ac_nonce`（**无 `uifid`**）
- `GET https://www.douyin.com/` 不带 `__ac_signature` 再请求 → 6313 字节验证码中间页

**已实测排除的假设**（都不是原因）：

| 尝试 | 结果 |
|---|---|
| 伪造 `uifid` cookie | 403 不变 |
| 伪造 `odin_tt` / `msToken` / `s_v_web_id` | 403 不变 |
| 加真实 `a_bogus`（用官方 SDK 现算） | 403 不变（且 detail 本来就该不带） |
| 加 `a_bogus` 各种长度（4/8/64/196 字符） | 4 字符→200 空；8~196→403 |
| 只带 `msToken`（107 字符） | 403 不变 |
| 走 `ttwid.bytedance.com` 的 `redirect_url` 回调 | **403 → 200 空 body**（确实清掉了一层门，但仍需 `uifid`） |

**影响**：验收项"可以读取 Aweme detail"、"可以获取点赞/评论统计"、"可以读取一级评论"、
"可以读取二级评论"、"可以播放 H.264 视频"全部**未达成**。

**为什么这不是客户端缺陷**：请求构造、参数顺序、`encodeURIComponent` 编码、cookie 作用域、
UA 一致性都已被 **Feed 的 200 + 234 KB 真实数据**证明正确。缺的是一条凭据获取链路。

**补法**（按可行性排序）：

1. **在真机上跑一次浏览器**（`Project-Petrichor` / Apotheosis 的 WebKit 移植或系统 Edge），
   用真实的 webmssdk 环境访问 douyin.com，导出 `uifid` 与 `__ac_signature`。
   这与 brief 第 2 条不冲突：brief 禁止的是**把 WebKit 做成项目主运行时**，
   而不是禁止用它做一次性的凭据导出。建议实现为显式的 `ICredentialSeedAdapter`。
2. **完成 a_bogus 逆向**（见 A2），然后用它驱动 webmssdk 的注册流程。
3. 在能访问 douyin.com 且不被验证码拦的网络路径上跑 `online_gate.py`，
   若首页直接下发 `uifid`，则问题自解。

### A2. `a_bogus` 逆向未完成（当前不阻塞）

| 部分 | 状态 |
|---|---|
| 编码器 | ✅ **已验证** —— `current_encode` + 提取的 134 字节 `KEYSTREAM` 逐字节复现 6 个真实签名 |
| payload 构造 | ❌ **未解** —— `build_current_payload` 抛 `IncompleteReverseError` |

未解偏移（`_abogus_research.CURRENT_UNSOLVED`）：
UA 槽位 `10,11,50,51`；时间槽位 `12,15,32,35,37,39,42,43,44,68`（payload 长度不固定，
说明有个变宽十进制字段）；环境位 `18,21`；指纹尾 `76…115`；校验和 `133`；前缀分布。

已独立验证的结论：盐是 `"dhzx"`，流水线是 `SM3(SM3(query+"dhzx")+"dhzx")`，
字母表是 `ALPHABET_255`。**公开的 `f2` v0.0.3 实现是过期的**（本机用真实 SDK 输出反证过）。

**当前不需要它**，因为门是 `ttwid` 而不是签名。逆向产物已 vendored 到
`python/douyin/_abogus_research.py`，`signer_abogus.py` 会在它能真正签名时自动注册 provider。

**注意**：`hanzheng1954/douyin-abogus-analysis` 有可用的纯 Python VM 解释器，但**无许可证**
（"all rights reserved"），因此**没有** vendor 进本项目，只在 `_research/abogus/refs/` 留作参考。

---

## B. 未验证（缺真机）

### B0. 真机首次部署的实测结论（2026-10-07）

设备 `192.168.3.159`，Windows 10 Mobile **15254.603**（不是 10586）。完整手册见
[DEVICE-RUNBOOK.md](DEVICE-RUNBOOK.md)。要点：

| 项 | 结果 |
|---|---|
| appx 部署与注册 | ✅ `PackageOrigin=5`（侧载） |
| appx 签名 | **设备强制要求**；未签名 → `0x800B0100` |
| 框架依赖 `Microsoft.NET.CoreRuntime.1.1` | 必须**独立安装**；作为附加 part 上传不会被注册；装成功后**不被包列表枚举** |
| 应用激活与存活 | ✅ **曾成功启动**，进程 28.6 MB 工作集，轮询 8 秒仍在 |
| appx 布局 | 包根 10 KB 原生 stub + `entrypoint\` 316 KB 托管 IL —— app-local-CLR 的 Debug 布局，正确 |
| `python-uwp.dll` 依赖 | `VCRUNTIME140_APP.dll`、`z.dll`、`bcrypt`、`RPCRT4`、`WS2_32` |
| `UseAppLocalCoreFramework` | 在此工具链上是**空操作**（CoreRuntime SDK 的 `AppLocal\arm` 只有 `UWPShim.exe`，无 CLR） |

**当前阻塞**：应用随后出现**启动即闪退**（`HTTP 500 0x8027025B`）。
设备上已装的**原生**应用 `EdgeHTMLReborn.Harness` 也启动即退，设备上有 6 个它的崩溃转储，
所以要先排除设备层面的原因。隔离手段是 `disable-python.txt` 诊断开关（见 RUNBOOK §3.3）。

**尚未取得**：`ttwid` 是否到手、协议层能否在嵌入式解释器里自检通过。
两者都由 `startup-report.txt` 回答，但应用还没活到写报告那一步。

### B1. 真机从未完整运行过

设备曾可达、appx 曾成功激活，但**核心链路（CPython 初始化 → 协议层自检 → ttwid → Feed）尚未在真机上走通**。
本机 x64 已经走通（§B5b），所以缺口集中在 ARM32 + AppContainer 这一层：

- CPython 3.15 在 10586/15254 的 ARM32 上能初始化
- AppContainer 沙箱下的路径解析与文件写入
- 传输层能收到 `Set-Cookie`
- `MediaElement` 真的能解码 H.264
- `IRandomAccessStream` 适配真的被 `MediaElement` 接受
- 连续刷视频内存不增长

### B5b. 本机验证：嵌入式 CPython 3.15 + 扩展 ABI + 协议层（**已通过**）

设备不可达时能取得的最强证据：把**同一份** `desultory-driver.c` 编译到 x64，
链接上游的 x64 `pythoncore.lib`（真实 CPython 3.15），用一个 C 宿主通过
`PyUwpRuntime_CallJsonEx` 驱动协议层。宿主用 `LoadLibrary`/`GetProcAddress`，
所以"它能跑"就意味着 C# 的 P/Invoke 绑定不会因 ABI 原因失败。

```
pwsh -NoProfile -File scripts\test-pythonhost-desktop.ps1     # 59/59
```

已验证（2026-10-07）：

| 项 | 结果 |
|---|---|
| CPython 3.15 启动 | `3.15.0b4+dev (main, Aug 5 2026) [Clang 22.1.7]` |
| 扩展 ABI `PyUwpRuntime_CallJsonEx` | 8 个导出符号全部解析并工作 |
| `douyin.protocol` 在嵌入式解释器内导入 | 通过（**不是**桌面 3.13 解释器） |
| `sys.path` | `<root>\Lib` → `<root>\AppLib` → `<root>\site-packages`，顺序正确 |
| 进程内自检 | 8 项全过 |
| 大响应（1.38 MB） | 通过 —— 这正是扩展 ABI 存在的理由（原 ABI 只有 64 KiB stdout 缓冲） |
| 中文 + emoji 往返 | 通过（评论正文、作者昵称、视频描述） |
| 重复发布守卫 | 通过（含 `duplicate-suppressed` 与窗口过期） |
| 签名策略 | 通过（detail `omit`、comment_list `presence`） |
| Argus 门分类 | 通过（识别出缺失凭据是 `uifid`） |

沙箱能力探测的实际输出（本机 x64，设备上应一致）：

```
_ssl-no ssl-no _sqlite3-no sqlite3-no _socket+yes socket+yes zlib+yes
_hashlib-no hashlib+yes json+yes urllib.parse+yes select+yes ctypes-no lzma-no bz2-no
```

**这直接证实了本项目的三条架构决策**：

- `_ssl` 不存在 → TLS **必须**由 native（C#）承担，Python 侧不能碰 socket。✅
- `_sqlite3` 不存在 → 持久化**必须**用文件型 store。✅
- `_hashlib` 不存在，但 `hashlib` 可用（由 `_md5`/`_sha1`/`_sha2`/`_sha3`/`_blake2` 内置后端支撑）
  → 签名所需的哈希原语齐全，无需移植 OpenSSL。✅

这个测试还抓到两个真实问题（都已修）：

1. **`main(int, char**)` 收到的是 ANSI 代码页**，把非 ASCII 路径当 UTF-8 转换会破坏它
   （本仓库在 `项目` 目录下），症状是 `import douyin` 报 "No module named 'douyin'"
   而解释器本身完全正常。改用 `wmain` 后解决。**这个坑在真机上不存在**，因为 C# 传的是
   宽字符串；但它提醒了任何用窄字符 argv 的宿主都要注意。
2. **`protocol.handle_json` 的错误路径用了非紧凑 JSON**（`json.dumps` 默认带空格），
   与其它所有响应不一致。已统一为紧凑分隔符。

### B5. 桌面测试跑在 3.13 的标准库上，不是 3.15 的

### B2. `Set-Cookie` 的收集方式未经真机确认

`UwpHttpTransport` 设了 `CookieUsageBehavior = NoCookies`（必须如此，见 HANDOFF §10），
然后从响应头枚举里读 `Set-Cookie`，并把 `HttpCookieManager` 作为第二来源。

**不确定点**：`NoCookies` 下平台是否仍把 `Set-Cookie` 留在响应头里。
（`Windows.winmd` 里 `HttpResponseHeaderCollection` 没有类型化的 `SetCookie` 成员，
所以只能靠通用头枚举；`HttpCookieManager.SetCookie` 存在但那是 setter。）

**若真机上收不到**：表现为 `ttwid` 永远为空 → 每个 API 调用 403。
`SessionManager` 会明确 Warn 出 "no ttwid was obtained"，一眼可见。

**补救**：改回默认 `CookieUsageBehavior`，改为在每次请求前清空 WinRT jar
（`CookieManager` 没有 clear-all，需要按名删除 —— 因此不推荐），
或者实现一个显式的 `CookieManager` → `Set-Cookie` 行重建（现有 `CollectCookies` 已经会做）。

### B3. Release|ARM 未构建

只验证了 `Debug|ARM`（`UseDotNetNativeToolchain=false`）。Release 配置在 csproj 里保留
（`UseDotNetNativeToolchain=true`）但未构建 —— .NET Native 会引入元数据保留问题，
改之前先确认 `Properties\Default.rd.xml` 覆盖了 Newtonsoft。

### B4. 写路径（发布评论）从未对真实账号执行

只做了只读探测（确认端点形状与参数名）。**没有用真实账号发过任何评论**，
这是刻意的。因此：发布是否真的成功、`status_msg` 的审核拒绝措辞、`a_bogus` 在写路径
是否只需 presence —— 全部未验证。

---

### B5. 桌面测试跑在 3.13 的标准库上，不是 3.15 的

`python/tests/run_tests.py` 用桌面解释器（本机 3.13），所以它验证的是**协议逻辑**，
不是"3.15 的 `Lib` 树里有没有这个模块"。这两件事不同，而差异只在真机上暴露。

已经做过一次显式审计来堵这个洞（2026-10-07）：

- 取协议层完整导入闭包（导入全部 16 个模块 + 跑一次 `diagnostics.selfTest`），
  得到顶层模块集合。
- 与 `cpython/Lib` 逐项比对，并与桌面 3.13 的 `Lib` 做差集，找出"3.13 有、3.15 没有"的模块。
- 结果：差集只有 `_compression` / `sre_compile` / `sre_constants` / `sre_parse` 四个，
  **全部是 3.13 起已废弃、3.15 已移除的模块，闭包不依赖任何一个**。
- 逐文件核对 appx 内 79 个必需 `.py`，只有 3 个"缺失"是误报：
  `math.py` / `itertools.py` 是 C 模块（无 .py），`collections/abc.py` 的真实实现在
  `_collections_abc.py`（已确认在包内）。
- 一个**踩到的坑**值得记下：`string` 在 3.15 里从模块变成了**包**
  （`Lib/string/__init__.py`，另带 `templatelib.py`）。只按 `string.py` 找会误判为缺失。
- `_pyrepl` 被排除，且闭包确认不依赖它（也不依赖 `_colorize` / `tokenize` / `inspect` /
  `ast` / `dis` / `typing` / `dataclasses`）。

**仍未覆盖**：这只证明了"模块在不在"。**但 §B5b 的 x64 宿主冒烟已经补上了更强的一环** ——
它让协议层在**真实的嵌入式 CPython 3.15**里导入并执行（而不是桌面 3.13），
所以"3.15 语义与 3.13 不一致"这个风险已经被实测覆盖：中文/emoji 往返、
哈希、`urllib.parse`、`json`、正则、大响应全部通过。

剩下的唯一差异是 **ARM32 与 x64 的浮点/整数行为**，对协议层无影响
（它只做哈希、字符串和整数运算）。

---

## C. 设计上未做（不在第一阶段范围）

| 项 | 说明 |
|---|---|
| 扫码登录 | 实测已死（`get_qrcode` 返回 JS 应用页）。状态机与解析器保留，`QR_FLOW_AVAILABLE = False` 是唯一开关 |
| `Lib` 进一步裁剪 | 现在 431 个文件 / 12.7 MB。若还要小，可考虑排除 `email`、`http`、`logging`、`xml` 等，但收益小、风险变高，不做 |
| `_sqlite3` 等价物 | 用文件型 store。元数据一 aweme 一文件、图片 LRU、历史单文件 |
| 真机 `_socket` 可用性 | 静态核含 `_socket`（本机已实测 `_socket+yes`），但客户端**不使用**它（TLS 走 native）。若将来要用 Python 侧网络，需在 10586 上验证 |
| 完整用户主页 / 搜索 / 点赞 / 收藏 / 关注 | brief 明确暂不要求。API 架构不阻止：加一条 `SIGNATURE_POLICY` 表项 + 一个 `EndpointVariant` + 一个 normalize 函数即可 |

---

## D. 下一步建议顺序

1. **拿到一台能上 douyin.com 的 10586 设备**，装 appx，看诊断页的 `ttwid` 是否到手（B2）。
   本机 x64 已经证明整条桥接与协议层可用，所以真机这一步现在只差"沙箱 + ARM32"两件事。
2. **导出一次真实 `uifid`**（A1 的补法 1），打通 detail 与评论 → 解锁 5 个验收项。
3. 真机验证播放链路（B1 的 `MediaElement` + `IRandomAccessStream` 两项）。
4. 真机验证内存（连续刷 100 条 + 连续播放）。
5. 用真实账号验证一次发布与回复（B4）。
6. 若风控收紧到要求真签名，再回头完成 A2，然后加一条
   `SIGNATURE_POLICY["aweme_detail"] = POLICY_REQUIRED`。
