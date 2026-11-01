# Project-Desultory-Signals

非官方抖音客户端核心层，目标 **Windows 10 Mobile Build 10586 (Version 1511) / ARM32 / UWP App Container**。

本仓库是**第一阶段**产物：CodeBehind + 核心业务逻辑 + CPython 3.15 集成 + Douyin Web API + 签名与 Session + Feed + 视频详情 + 评论 + 登录 + 发布评论 + 视频播放链路 + 缓存 + 错误恢复。**不含正式 UI**（只含最简调试壳）。

## 硬约束

| 约束 | 内容 |
|---|---|
| 目标平台 | Windows 10 Mobile 10586，ARM32，UWP App Container |
| 不兼容 | Windows Phone 8.1、14393+ 专有 API、WinUI、WebKit、外部服务器 |
| 运行时 | CPython **3.15**（AlterGate/PyUWP 移植，静态核，非降级） |
| 协议 | 自构造请求 + 自维护 Cookie + 自算 a_bogus + 自解析 JSON |
| 传输 | TLS 由 native（C# `Windows.Web.Http`）承担；Python 侧**没有 `_ssl`** |
| 持久化 | 文件型 store（等价方案；Python 侧无 `_sqlite3`） |

## 目录

```
src/DesultorySignals.Core/    核心逻辑（netstandard1.4，桌面可单测）
  Models/ Douyin/ Networking/ Media/ Services/ Python/ Diagnostics/
python/douyin/                嵌入设备的纯 Python 协议层（仅标准库）
app/DesultorySignals/         C# UWP 10586 调试壳（CodeBehind + 平台适配）
tests/                        桌面测试运行器
native/pythonhost/            扩展的 CPython 宿主 ABI（源码）
script(s)/                    构建 / 测试 / 打包脚本
docs/                         架构、API surface、已知限制
_research/                    逆向研究产物（a_bogus 等）
```

## 当前状态

| 项 | 状态 |
|---|---|
| ARM32 CPython 3.15 静态核 | ✅ 构建通过（`pythoncore.lib` 12.65 MB） |
| 扩展宿主 ABI | ✅ 构建通过（`native/out/python-uwp.dll` 5.07 MB, ARMNT AppContainer） |
| Python 协议层 | ✅ **187/187** 断言通过（桌面解释器） |
| C# 核心层 | ✅ 构建 0 错误；**237/237** 断言通过 |
| **嵌入式 CPython 3.15 运行时** | ✅ **59/59** —— 本机 x64 上真实跑通运行时 + 扩展 ABI + 协议层 |
| 10586 ARM appx | ✅ 构建并**签名**（12.7 MB），MinVersion 10586，含 `Lib` 431 文件 + `AppLib/douyin` 17 模块 |
| Feed（真实数据） | ✅ **真实网络实测** 200 + 234 KB，真实标题/点赞/评论数 |
| aweme detail | ✅ 真机使用最小查询取得真实详情 |
| 评论列表 | ✅ 真机一级评论、分页与 v2 回复成功 |
| 视频播放 | ✅ 真机流式及下载播放，用户确认画面正常；暂停探针已修正，待再次完整采集 |
| 真机验证 | ✅ 15254.603 上启动、CPython、ttwid 和 Feed 正常；10586 尚未实机验证 |
| 搜索 / 主页 | 已接入界面及 API；游客视频搜索要求登录，作者资料可读，作品列表仍需修复 |
| 扫码登录 | 原生桌面 Passport 方案生成及未扫码轮询成功；扫码确认和会话验证进行中 |

第一阶段历史基线合计 **483** 项断言通过（187 + 237 + 59）；本次修复后未重跑桌面套件。当前结果以 [启动日志](crash/device-startup-report.txt)、[设备报告](crash/device-device-check-report.json) 与 [登录报告](crash/device-login-check-report.json) 中的包版本为准，勿把旧报告视为新版本验证。

> **真机排障请看 [docs/DEVICE-RUNBOOK.md](docs/DEVICE-RUNBOOK.md)**：六个 WDP 的坑
> （CSRF 从响应头读、手工 multipart、PRAID 启动、框架包必须独立安装且不被枚举、appx 必须签名、
> 取文件的两个参数）、崩溃转储分析、以及 `disable-python.txt` 诊断开关。

### 本机验证（设备不可达时能做的最强验证）

把**同一份** native 驱动编译到 x64，链接真实 CPython 3.15 静态核，用一个 C 宿主
通过 `PyUwpRuntime_CallJsonEx` 驱动协议层：

```
pwsh -NoProfile -File scripts\test-pythonhost-desktop.ps1
```

已验证：CPython `3.15.0b4+dev [Clang 22.1.7]` 启动、`douyin.protocol` 在**嵌入式**解释器内
导入并执行、1.38 MB 响应往返（原 ABI 上限 64 KiB）、中文+emoji 全程无损、重复发布守卫、
签名策略、Argus 门分类。

沙箱能力探测同时**证实了三条架构决策**：

```
_ssl-no  _sqlite3-no  _hashlib-no 但 hashlib+yes  _socket+yes  zlib+yes  json+yes
```

`_ssl` 缺失 → TLS 必须由 native 承担；`_sqlite3` 缺失 → 用文件型 store；
`hashlib` 由内置 `_md5`/`_sha2`/`_sha3`/`_blake2` 支撑 → 签名原语齐全，无需移植 OpenSSL。

### 最重要的实测发现

**真正的门禁是服务端下发的 `ttwid` cookie，不是合法签名**，而且**在不需要签名的端点加签名会让请求从 200 变成 403**。
因此签名策略是**按操作配置的数据表**（`python/douyin/endpoints.py` 的 `SIGNATURE_POLICY`），
取值 `omit` / `presence` / `required`。详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

### 已知阻塞

`aweme/detail` 与评论接口被 Argus 安全门拦下，它额外要求 `uifid`。
`uifid` 只能由 webmssdk 或通过 `__ac_signature` 验证码页的首页下发；
本机网络路径上首页返回 JSVM 引导页并只设 `__ac_nonce`。伪造 `uifid`/`odin_tt`/`msToken`/`s_v_web_id` 均已实测失败。

这不是客户端缺陷：请求构造、参数顺序、编码、cookie 作用域、UA 都已被 Feed 的 200 证明正确。

## 部署到真机

```powershell
# appx 已用自签开发证书签名；设备上装一次证书即可
#   app\DesultorySignals\DesultorySignals_TemporaryKey.cer
# 然后用输出目录里的 Add-AppDevPackage.ps1，或 Device Portal 上传 appx
```

真机需要 `Microsoft.NET.CoreRuntime.1.1 (ARM)`（MinVersion 10.0.10049 → 10586 兼容），
构建输出目录的 `Dependencies\ARM\` 里已带上。

## 文档

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) —— 架构与每个设计决策的理由
- [docs/API-SURFACE.md](docs/API-SURFACE.md) —— 实际使用的每一个接口 + 证据强度
- [docs/DEVICE-RUNBOOK.md](docs/DEVICE-RUNBOOK.md) —— **真机部署与排障手册（实测）**
- [docs/DEFERRED.md](docs/DEFERRED.md) —— 已知限制与后续工作
- [HANDOFF.md](HANDOFF.md) —— **给第二阶段 UI Agent 的交接**
- [_research/abogus/EVIDENCE.md](_research/abogus/EVIDENCE.md) —— 签名逆向的完整证据与失败记录

## 目录

```
src/DesultorySignals.Core/    核心逻辑（netstandard1.4，桌面可单测）
python/douyin/                协议层（仅标准库，无 socket 无 ssl）
app/DesultorySignals/         C# UWP 10586 调试壳
native/pythonhost/            扩展的 CPython 宿主 ABI
tests/                         C# 桌面测试运行器
python/tests/                 Python 测试 + 真实网络 gate 检查
_research/abogus/             签名逆向证据
scripts/                      构建脚本
```

设计要点见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，给第二阶段 UI Agent 的交接见 [HANDOFF.md](HANDOFF.md)。

## 构建

```powershell
# 1. ARM32 CPython 静态核（一次，耗时几分钟）
pwsh -NoProfile -File scripts\build-pythoncore.ps1

# 2. 扩展宿主 ABI → python-uwp.dll（需要第 1 步的 pythoncore.lib）
pwsh -NoProfile -File scripts\build-pythonhost.ps1

# 3. 测试（三套，全部不需要真机）
pwsh -NoProfile -File scripts\test-all.ps1

# 4. UWP appx（ARM，MinVersion 10586），构建后自动签名
pwsh -NoProfile -File scripts\build-app.ps1
```

也可以单独跑某一套：

```powershell
pwsh -NoProfile -File scripts\test-python.ps1              # 协议层逻辑（桌面 3.13）
pwsh -NoProfile -File scripts\test-python.ps1 -Online      # 额外打真实 API
pwsh -NoProfile -File scripts\test-core.ps1                # C# 核心
pwsh -NoProfile -File scripts\test-pythonhost-desktop.ps1  # 嵌入式 CPython 3.15 + 宿主 ABI
```

## 来源与许可

- `cpython/` 源码不在本仓库，来自外部 AlterGate 移植树（默认 `H:\项目\UWP\Python UWP`），构建时通过 `-PythonSrc` 指定。
- `pythonhost/` 中的 `pyuwp-shim.h` / `pyuwp-stubs.c` 源自同一移植树，见文件头 provenance 注释。
