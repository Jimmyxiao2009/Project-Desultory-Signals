# 启动闪退诊断（2026-10-07）

## 修复完成记录

补上 Microsoft.VCLibs.140.00 的 Retail ARM 包依赖后，手机正常进入调试页并运行 CPython、Feed 和播放。实际二进制导入包含 VCRUNTIME140_APP.dll，原 manifest 的包依赖图只声明 CoreRuntime；依赖图补全与修复后成功启动共同支持该定位。

原始 ETW 仅直接证明缺 DLL 的退出码，不直接给出 DLL 名称。下文保留修复前诊断过程。

构建脚本校验并暂存 Microsoft 签名的 ARM VCLibs。手机已有更高版本时不降级；重复应用更新可复用已安装框架。部署脚本不会清空 LocalState。

## 结论与证据强度

**已确认：本次启动失败发生在原生 DLL 加载阶段，退出码为 `0xC0000135 / STATUS_DLL_NOT_FOUND`。**
首要定位是 `VCRUNTIME140_APP.dll` / ARM32 Retail VCLibs 依赖未进入应用的包依赖图。
具体 DLL 文件名尚未从设备日志直接取得，因此不能将这一文件名当作已完成修复验证的结论。

目标设备：`192.168.3.159`，Windows 10 Mobile `15254.603`。
目标包：`DesultorySignals.DebugShell_0.1.0.0_arm__war90cckbyaqp`。

## 本轮真机实测

1. 正常启动返回 HTTP 500，错误 `0x8027025B`（The app didn't start）。
2. LocalState 起初为空，没有 `site-packages`，没有 `startup-report.txt`。
3. 经 WDP 上传 `disable-python.txt` 并确认文件存在后再次启动，仍返回相同错误；仍未创建上述目录或报告。
4. 经 WDP WebSocket 捕获 ETW：启动进程 PID **3136**，创建时间 **2026-10-07 11:14:11.0542185 UTC**，退出时间 **11:14:11.074979 UTC**，寿命约 **20.76 ms**。
5. `Microsoft-Windows-Kernel-Process / ProcessStop` 的 `ExitCode` 为 **3221225781 = 0xC0000135**；此前出现 `Microsoft-Windows-User-Loader` 的 ID 7 警告。
6. 本应用的崩溃收集已开启，但列表仍只有 Harness 转储，未产生本应用转储。不能把其他包的转储当成本应用证据。
7. 实验结束后已删除本轮创建的 `disable-python.txt`，确认 LocalState 恢复为空，并结束卡住的本机诊断连接。

关键事件：[activation-summary.json](../crash/activation-summary.json)。
完整接口事件：[activation-events.jsonl](../crash/activation-events.jsonl)。

手机 WDP 的部分事件解码有问题：字符串字段为空，有些数值字段混入 UTF-16 字符（例如 `0x650044` 实际对应“De”）。这些字段不能直接用作真实 HRESULT 或文件名。ProcessStop 的固定数值字段能够给出缺 DLL 的退出码，但日志没有提供可用的缺失 DLL 文件名。

## 为什么优先定位 VCLibs

实际 appx 内的包根 `DesultorySignals.exe` 是 **10240 字节的原生 UWPShim 启动器**，真正的托管程序在 `entrypoint/DesultorySignals.exe`。
通过 `llvm-readobj --coff-imports` 确认启动器直接导入：

- `VCRUNTIME140_APP.dll`
- `UWPHOST.dll` 的 `ExecuteAssembly`
- 系统 API set DLL

实际 appx 清单只有 `Microsoft.NET.CoreRuntime.1.1` 框架依赖，**没有 VCLibs 依赖**。appx 内也没有 `VCRUNTIME140_APP.dll`。
随包的 ARM CoreRuntime 框架包含 `UWPHost.dll` / `coreclr.dll`，但不包含 `VCRUNTIME140_APP.dll`，它自身也没有声明 VCLibs 依赖。
此外 `python-uwp.dll` 和 `z.dll` 同样导入 `VCRUNTIME140_APP.dll`。

这说明 VCLibs 的缺口不仅影响 Python：原生启动器本身就需要它，足以在进入 C# App、XAML 页面和 Python 引导之前退出。
禁用 Python 的对照实验以及约 21 ms 的加载器退出与这个定位一致。

本机当前 VCLibs 14.0 ExtensionSDK 的 SDKManifest 虽写有 ARM 路径，但相应 ARM 包实际缺失；目前目录里只有 x86/x64/ARM64 包。不能把“构建成功”当作 ARM 运行时依赖齐全的证明。

## 修复建议与剩余验证

1. 取得 Microsoft 官方、兼容目标系统的 **ARM32 UWP Retail** `Microsoft.VCLibs.140.00` 框架包，并确认它包含 ARM32 `VCRUNTIME140_APP.dll`。这里导入的是 Retail 文件名，没有 `D` 后缀；仅安装 Debug VCLibs 不足以满足它。ARM64 与 Desktop 框架均不能替代。
2. 在源 `Package.appxmanifest` 的 Dependencies 中显式声明 `Microsoft.VCLibs.140.00` 的 PackageDependency，Publisher 用微软框架 Publisher；MinVersion 按实际选取的兼容 ARM 框架填写。要在最终 appx 清单中确认它保留下来。
3. 独立部署对应框架，再构建、签名、部署应用，重做禁用 Python 的启动对照。只安装框架而不声明依赖，不能保证它进入应用 DLL 搜索路径。
4. UI 能存活后恢复 Python 引导，检查原生初始化与 startup-report。若仍出现 `C0000135`，继续检查 UWPHost/CoreRuntime 和系统 API set 的依赖链。

本轮只完成诊断，没有修改源码、重新打包或替换手机上的应用；因此尚未证明修复后成功启动。

## handoff 中需要纠正的判断

- “原生 Harness 也闪退 → 设备/系统层面”不成立；两个应用可能各自缺依赖或各有代码故障，必须分别检查退出码或转储。
- “加入启动报告后开始失败”只是时间顺序，不能证明报告代码导致崩溃。本次进程在原生加载阶段退出。
- `UseAppLocalCoreFramework=true` 的 csproj 注释声称 CLR 会随包携带，但实际 appx 没有 coreclr，仍声明 CoreRuntime 框架依赖。应以最终 appx 为准。

## 官方依据

- [Microsoft：DLL 搜索顺序与包依赖图](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-search-order)
- [Microsoft：C/C++ 缺 DLL 启动故障](https://learn.microsoft.com/en-us/cpp/build/troubleshooting-c-cpp-isolated-applications-and-side-by-side-assemblies)
- [Microsoft：Device Portal REST 与 ETW WebSocket](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/device-portal-api-core)
