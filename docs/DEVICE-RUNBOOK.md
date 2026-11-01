# 真机操作手册（Windows 10 Mobile）

本文件记录**实测**得到的部署与调试流程。每一条都是踩过之后写下来的，不是推测。
目标设备：`192.168.3.159`，Windows 10 Mobile **15254.603**（RS3），Device Portal 在 `https:443`，无密码。

> 注意：设备实际是 **15254**，不是 10586。appx 的 `MinVersion=10.0.10586.0` 向前兼容，所以能装能跑；
> 但这意味着到目前为止的**真机验证覆盖的是 15254**。10586 上的行为仍未验证（见 `DEFERRED.md`）。

---

## 最新修复后的操作

```powershell
py -3.13 -X utf8 -u scripts/device-check.py status
py -3.13 -X utf8 -u scripts/device-check.py deploy --appx <主应用ARM安装包>
py -3.13 -X utf8 -u scripts/device-check.py start-check
py -3.13 -X utf8 -u scripts/device-check.py collect
py -3.13 -X utf8 -u scripts/device-check.py start-login
```

`start-check` 只在明确请求时上传一次性标记，执行只读接口和视频播放检查；普通启动不自动播放。`start-login` 自动打开原生扫码界面，需要用户用另一台已登录抖音的手机扫码。二维码 token、二维码原文及 Cookie 值不写入报告。

`collect` 报告保存在 `crash/device-*`。请核对报告 package 版本，旧的 .6 播放报告不会因安装新包自动变成新结果。

当前启动已恢复，详情、评论、回复和两条播放路径已有真机证据；搜索、作品列表及登录确认仍继续验证。以下记录保留第一阶段部署排障细节。

## 1. 一条命令部署

```powershell
# 构建（自动签名）
pwsh -NoProfile -File scripts\build-app.ps1 -Configuration Debug

# 部署 + 启动 + 取回启动报告
pwsh -NoProfile -File scripts\deploy-device.ps1 -DeviceIp 192.168.3.159 -Launch -PullDiagnostics

# 只看设备状态
pwsh -NoProfile -File scripts\deploy-device.ps1 -DeviceIp 192.168.3.159 -List

# 只启动（不重新部署）
pwsh -NoProfile -File scripts\deploy-device.ps1 -DeviceIp 192.168.3.159 -SkipDeploy -Launch
```

`deploy-device.ps1` 会**自动**做三件容易漏掉的事：先装 `Microsoft.NET.CoreRuntime.1.1`（框架包必须
先于依赖它的应用安装）、带上 `-NoPackageCheck` 时不做包列表校验、启动时用正确的 PRAID。

---

## 2. 六个必须知道的 WDP 细节

### 2.1 CSRF token 必须从响应头读，不能靠 CookieContainer

```powershell
$handler.UseCookies = $false          # 关键
$handler.ServerCertificateCustomValidationCallback = ...DangerousAcceptAnyServerCertificateValidator
$response = $client.GetAsync("$base/api/os/info")...
$values = $null
$response.Headers.TryGetValues('Set-Cookie', [ref]$values)
$csrf = ([regex]'CSRF-Token=([^;,\s]+)').Match(($values -join ';')).Groups[1].Value
```

两个坑叠在一起：

- **`UseCookies = true` 时 .NET 会把 `Set-Cookie` 从响应头里删掉**并塞进 CookieContainer，于是 token 凭空消失。
- 就算它没删，**`CookieContainer` 对 IP 形式的主机不可靠**（它的域匹配规则是为域名设计的），
  cookie 会静默地进不去。所以宁可完全不要 cookie jar。

没有 `X-CSRF-Token` 头的 POST 会得到 `403 CSRF Token Invalid`。

### 2.2 multipart 必须手工构造，字节级对齐 curl

WDP 靠 `Content-Disposition` 的 **filename** 识别载荷。.NET 的 `MultipartFormDataContent` 在值
是合法 HTTP token 时**不加引号**，而 `Microsoft.NET.CoreRuntime.1.1.appx` 正好是（点是 token 字符）：

```
.NET 生成:  Content-Disposition: form-data; name=X.appx; filename=X.appx
WDP 需要:   Content-Disposition: form-data; name="X.appx"; filename="X.appx"
```

差别导致 WDP 回 `400 {"Reason":"Missing .appx (or .eappx) file in uploaded files"}`。
.NET 还会给 boundary 加引号。所以 `scripts\deploy-device.ps1` 直接拼字节（`New-MultipartBody`），
参照 Apotheosis 的 `tools/wdp.sh` 里那条 curl 命令——那是最权威的格式说明。

### 2.3 启动应用：appid 是 PRAID，且两个参数都在查询串里

```powershell
$appId   = $package.PackageRelativeId      # "<FamilyName>!App"，从设备读，不要自己拼
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($appId))
$pkg     = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($fullName))
POST "$base/api/taskmanager/app?appid=$encoded&package=$pkg"
```

- 放 **查询串**，不是表单体。
- `appid` 必须是 **PRAID**（`DesultorySignals.DebugShell_war90cckbyaqp!App`），不是字面量 `"App"`。
  发 `"App"` 得到 `400 {"Reason":"Missing app id argument."}`。
- 两个值都要 base64。PRAID 里的 publisher hash 无法预测，必须从设备读。

### 2.4 框架包必须单独装，且不会被包列表枚举

`Microsoft.NET.CoreRuntime.1.1` 是 Debug（非 .NET Native）托管 UWP 应用的**清单声明的依赖**。
设备上没有它时，应用能装、能注册，但**激活失败**：`HTTP 500 / 0x8027025B "The app didn't start."`

两个反直觉之处：

- 把它作为 appx 部署请求的**附加 part** 一起上传**不会注册它**。请求被接受、状态报 Success，
  然后应用照样起不来。必须作为**独立的包**先装。
- 装成功后 **`/api/app/packagemanager/packages` 不会列出它**（框架包被隐藏）。
  所以用"包列表出现"作为完成判据会一直等到超时，即使实际成功。这就是 `-NoPackageCheck` 存在的原因。

### 2.5 appx 必须签名

设备对未签名包直接拒绝：

```
error 0x800B0100: The app package must be digitally signed for signature validation.
```

用 `app\DesultorySignals\DesultorySignals_TemporaryKey.pfx`（密码 `desultory`），
由 `scripts\build-app.ps1` 调 `signtool` 完成。**MSBuild 15 自带的签名不能用**：它的 PFX 导入器
读不了现代 PFX（`APPX0105 "可能受密码保护"`，而同一个文件在别处能正常打开）。所以工程里
`AppxPackageSigningEnabled=False`，签名放在脚本里——顺带让签名步骤可见、可复现。

### 2.6 取 LocalState 文件：目录和文件名是两个参数

```
GET /api/filesystem/apps/file?knownfolderid=LocalAppData
    &packagefullname=<PackageFullName>
    &path=%5CLocalState          ← 目录（\LocalState）
    &filename=startup-report.txt ← 文件名，单独给
```

把文件名塞进 `path` 会得到 `400 "Missing known folder id"`——报错信息在误导（knownfolderid 明明给了）。

---

## 3. 应用启动不起来时怎么查

### 3.1 先做对照实验

设备上已装的 `EdgeHTMLReborn.Harness`（原生 C++/CX）**也**是起来就消失，设备上还有 6 个
`Harness.exe.*.dmp`。所以"启动即退"在这台设备上**不是本应用独有**。

对照实验能立刻把问题分成两类：

- 原生应用也起不来 → 设备/系统层面（缺 CRT 框架、系统组件损坏、设备状态）
- 只有本应用起不来 → 本应用特有（打包、依赖、代码）

### 3.2 用崩溃转储定性

```powershell
# 开收集（一次即可）
pwsh -NoProfile -File E:\Apotheosis\tools\Wdp-Crash.ps1 -Enable -Match 'Desultory' -DeviceIp 192.168.3.159
# 在设备上复现闪退
# 拉最新转储并用 cdb 分析
pwsh -NoProfile -File E:\Apotheosis\tools\Wdp-Crash.ps1 -Pull -Match 'Desultory' -DeviceIp 192.168.3.159
```

异常码本身基本就能定性（脚本注释里也写了）：

| 码 | 含义 |
|---|---|
| `C0000135` | 缺 DLL（打包问题） |
| `C0000139` | 导出不匹配（版本问题） |
| `C0000005` | 访问违例（空指针） |
| `C0000409` | `__fastfail` / RELEASE_ASSERT |
| `C06D007E` | 延迟加载模块缺失 |

**注意**：`/api/debug/dump/usermode/dumps` 的列表是**设备级**的，不按包过滤。查询我们包的转储时
返回的是 `Harness.exe.*.dmp`。用 `packageFullName` 过滤下载会 404。所以要么按文件名取，要么
在设备上只复现目标应用。

### 3.3 用启动报告隔离"UI 壳"与"Python 桥接"

这是本应用特有的、最有用的一个手段。原生崩溃**无法被托管代码捕获**——进程直接消失，
`UnhandledException` 都不会触发。所以需要能把 Python 整个摘出去再试一次。

```powershell
# 1. 让应用跳过全部 Python 引导（在 LocalState 里放一个空文件）
#    在设备上用文件资源管理器新建 disable-python.txt，或经 WDP 上传
# 2. 启动。UI 能正常显示 → 问题在 CPython 桥接；UI 仍闪退 → 问题在 XAML 壳
```

开关名与文件名在 `App.DisablePythonFile`，报告在 `App.StartupReportFile`。
之所以做成**读文件**而不是编译期常量：一次构建就能做 A/B，不用重编、重签、重装。

启动报告（`startup-report.txt`，JSON）逐字段回答"哪一步失败"：

```jsonc
{
  "engineStarted":       true,   // 引擎线程起来了
  "runtimeInitialized":  true,   // PyUwpRuntime_Initialize 成功
  "bridgeReady":         true,   // douyin.protocol 在嵌入式解释器里导入成功
  "selfTestOk":          true,   // 进程内 8 项自检全过
  "runtimeDescribe":     "...",  // 原生运行时自述
  "lastNativeError":     "...",  // PyUwpRuntime_LastError() 原文
  "identity": { "ttwid": "present|absent", "uifid": "present|absent", "msCookieCount": 3 },
  "feedProbe": { "items": 2, "total": 2 },   // 真打了一次 Feed
  "feedProbeError": "...",       // 或分类后的失败原因
  "log": "..."                   // DiagLog 全文（已脱敏）
}
```

`identity.ttwid` 是最重要的一个字段：**服务端不下发 ttwid 之前，所有 API 调用都会被拒**。

### 3.4 设备会反复掉线

症状组合很有特点：**`ping` 不通，但 443 端口是开的，SSL 握手失败**（省电导致的半死不活状态）。
不是设备关机，等一会儿会自己回来。部署脚本里的重试循环就是为这个加的：

```powershell
for($i=1;$i -le 6;$i++){
  $out = pwsh -NoProfile -File scripts\deploy-device.ps1 ...
  if($LASTEXITCODE -eq 0){ break }
  if($out -match 'SSL connection could not be established'){ Start-Sleep -Seconds 20; continue }
  break
}
```

---

## 4. 已确认的事实（实测，非推测）

| 项 | 结论 |
|---|---|
| 设备 | `192.168.3.159`，Windows 10 Mobile **15254.603** ARM |
| WDP | `https:443`，无密码，CSRF 走响应头 |
| 包安装 | ✅ `DesultorySignals.DebugShell_0.1.0.0_arm__war90cckbyaqp` 已注册（`PackageOrigin=5`） |
| 框架依赖 | `Microsoft.NET.CoreRuntime.1.1` 已装（`POST crashcontrol` 与状态查询均可用） |
| appx 签名 | 设备强制要求；未签名 → `0x800B0100` |
| 启动 | 装上框架后**曾返回 HTTP 200**，进程存活并占用 28.6 MB 工作集（轮询 8 秒仍在） |
| appx 布局 | 包根 `DesultorySignals.exe` = 10 KB **原生 stub**；真正的托管 IL 在 `entrypoint\DesultorySignals.exe`（316 KB）。这是 app-local-CLR 的 Debug 布局，**不是打包错误** |
| `python-uwp.dll` 依赖 | 导入 `VCRUNTIME140_APP.dll`（需要 `Microsoft.VCLibs.140.00`）、`z.dll`、`bcrypt`、`RPCRT4`、`WS2_32` |
| VCLibs | 本机 VCLibs 扩展 SDK **没有 ARM**（只有 ARM64/x64/x86），所以没生成 VCLibs 依赖 |
| `UseAppLocalCoreFramework` | 在此工具链上**是空操作**：CoreRuntime SDK 的 `AppLocal\arm` 里只有 `UWPShim.exe`，没有 CLR。所以框架只能装，不能随包走 |

---

## 5. 未解决

1. **应用闪退**（当前阻塞项）。装上框架后曾成功启动并存活；加入启动报告代码后，
   启动开始返回 `HTTP 500 0x8027025B`。需要：
   - 用 §3.3 的 `disable-python.txt` 做 A/B，判定是 XAML 壳还是 CPython 桥接；
   - 若怀疑崩溃循环保护，先 `DELETE /api/taskmanager/app?package=<pfn>` 再启动。
2. **原生应用（harness）也启动即退**，设备上有 6 个 `Harness.exe.*.dmp`。若连它都起不来，
   要先排除设备层面的问题（缺共享 CRT、系统组件损坏），再回头查本应用。
3. **未取得 `startup-report.txt`**：应用还没活到写报告那一步，所以 CPython 是否初始化、
   `ttwid` 是否到手，都还没有真机证据。
4. **10586 完全未验证**：目前所有真机结论都来自 15254。
