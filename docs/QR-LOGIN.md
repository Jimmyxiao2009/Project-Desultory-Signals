# 原生扫码登录调查与当前实现

## 实测链路

2026-10-07，原生界面使用官方 `imdesktop.douyin.com` 桌面 Passport 通道（aid 339757）：

- GET `/passport/web/get_qrcode/` 返回 `message=success`、PNG base64、临时 token、绝对 epoch 秒过期时间。
- POST `/passport/web/check_qrconnect/` 返回 `message=success`、`data.status=new`；两次主机真实轮询和 .11 真机轮询成功。
- 页面显示二维码，轮询遵循约 2 秒间隔与服务端过期时间；取消终止请求。
- `status=3/confirmed/success` 时允许完成抖音 HTTPS 域名内的登录跳转，逐跳接收 Cookie。只有实际 session Cookie 到达才标记 Confirmed。
- Cookie 由 SessionManager 保存，不写进日志；登录报告只含状态、二维码是否存在、过期时间和包版本。

**用户扫码后失败，尚未登录。** .11 真机日志在扫码后收到 HTTP 200、144 字节响应，`login.qrRedirect` 报 risk_control，服务端提示“系统繁忙，请重启应用或刷新页面后重试”。原日志未保留错误码，不能据此判定具体风控类型。

.12 修复了异常后 `login-check-report.json` 仍停留 WaitingForScan 的错误。`login.qrDiagnostic` 只记录 HTTP 状态、数字错误码、已知状态、验证字段是否存在；不记录响应原文、二维码 token、验证票据或账户值。失败停止轮询，清除二维码；取消等待间隔也会落盘失败状态。需要再次实际扫码取得错误码，随后才能判断是否应接入官方验证流程。Cookie、重启持久化与登录后的搜索均未通过验证。

## 不可用的两条尝试

- 老 `sso.douyin.com/get_qrcode/` 返回 SPA 页面。
- `login.douyin.com/passport/web/get_qrcode/` 加正常 web SDK 参数可生成二维码，但轮询始终返回 4031。
- 手机 WebView 的官网页面显示验证码登录。NavigateWithHttpRequestMessage 设置电脑 HTTP UA 后，JS navigator.userAgent 仍为 Windows Phone，未出现二维码。主扫码入口已从 WebView 切回原生图像。

## 请求校验

桌面通道使用 SDK 参数 checksum（sign/qs）及 `x-tt-passport-aid-sign`，与 Feed 的 a_bogus 策略独立。算法由项目自行实现，并用真实官方接口核对；未将占位 a_bogus 当作有效签名。

请求结构研究参考公开 SDK 作者的 [连接实现](https://github.com/zhinjs/douyin-im/blob/master/src/desktop/api-connection.ts) 与 [Passport 校验实现](https://github.com/zhinjs/douyin-im/blob/master/src/passport/signQs.ts)。

## 采集

```powershell
py -3.13 -X utf8 -u scripts/device-check.py start-login
py -3.13 -X utf8 -u scripts/device-check.py collect-login
```

不要将临时 token、二维码原文或 Cookie 值加入报告。扫码过期后用应用“扫码登录”重新创建，不重复使用旧 token。

## .12 扫码后响应与 .13 参数修正

真机 2026-10-07T13:22:50Z：`httpStatus=200,errorCode=2156,serverMessage=error`；primary/secondary/MFA 验证字段均不存在，最终 Failed、无二维码、仍为 guest。没有可打开的验证流程，2156 的内部含义尚无官方说明，不能推断账号已被封禁。

与公开 SDK 核对后，.13 将轮询表单 `is_frontier` 从 false 修正为 true，并补充本次二维码会话稳定的 trace-id/verify-portrait 关联头。它们只是请求关联 ID，不是设备信任证明；未伪造设备登记、验证码结果或登录 Cookie。这一差异是否导致 2156，必须以再次扫码后的真实响应确认。

### .13 复查结果

- 真机启动健康，Feed 2 条成功；2026-10-07T13:27:41Z 扫码轮询仍返回 `errorCode=2156`，最终 Failed、仍为游客。参数修正没有解决扫码后的拒绝。
- 主机读取同一游客身份，在内存中创建另一个未扫描二维码：连续四次轮询 HTTP 200、errorCode=0、status=new。每次 CSRF 头与该请求 Cookie 匹配。二维码 token 和凭据未保存或输出。
- 目前无法从公开官方说明确定 2156 的精确拒绝原因；不能据此声称账号被封禁，也不能将本地风险分类当成完整官方原因。
- 当前返回中没有 primary/secondary/MFA 挑战，客户端无可继续的官方验证步骤。登录仍需完整受支持的官网浏览器认证或有效 Cookie 导入；扫码功能未验收。

## 网页端验证（用户要求改试网页二维码）

- 电脑 Edge 实际打开 `https://www.douyin.com/login_page`，可见“扫码登录”、二维码、验证码与密码登录。已取得页面截图，尚未扫码确认。
- 官方前端 `main.498fc6f1.js` 的默认 accountApiConfig 为 aid=6383；normal SDK 的 host 默认当前页面 hostname，因此网页通道使用 www.douyin.com，相对接口 /passport/web/get_qrcode/ 与 /passport/web/check_qrconnect/。页面 SDK p_ui=2.4.10，账户 SDK 5.4.2。网页中的客户端安全 SDK 会参与请求；直接 HTTP 请求不等于完整浏览器链路。
- 新鲜 CookieJar 先 GET 官网 login_page，再用该网页通道参数 GET get_qrcode：HTTP 200、message=success、二维码和临时 token 均存在，响应设置 passport_csrf_token/default。
- 同一会话 POST check_qrconnect（is_frontier=true）在未扫码时返回 HTTP 200、errorCode=4031、message=error，无 primary/secondary/MFA 挑战。未将这一条不可用轮询路径部署成新版本；手机仍是 .13。
- 浏览器调试在尝试记录网络请求时持续超时（Accessibility.enable / Emulation.setFocusEmulationEnabled）；重置运行时并重新绑定页面后仍失败，因此未取得浏览器内的扫码后结果。已请用户直接在电脑官网刷新并扫码，以确认真实浏览器认证是否通过。
- 仅获得二维码不能算登录成功。网页端扫码后的 Cookie 导入和搜索仍未验证；不要声称网页登录已修复，也不要在聊天中索取 Cookie。

## 电脑验证码通过后的手机复查

用户报告已在电脑官网扫码并完成验证码验证。未自动读取或转移电脑浏览器凭据。

2026-10-07T13:53:49Z 手机 .13 正常启动、游客 Feed 2 条成功。重新建立手机原生桌面 Passport 会话后，前三次轮询 status=new；13:54:03Z 返回 errorCode=2156，状态 Failed，isLoggedIn=false，primary/secondary/MFA 字段均未出现。该轮未记录中间 Scanned 状态，不能仅靠它断言发生了何种扫码确认步骤。

结论：电脑验证通过未使这个手机登录会话通过。2156 和当前记录字段不能证明手机也只需短信验证码；需要取得手机官方验证挑战或采用完整浏览器认证。手机仍未登录，未声称搜索或作者作品已通过登录态验收。
