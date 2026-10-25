# 应用更新

自 `0.1.0-alpha.3 / 构建 3` 起发布 Qt 版。其构建入口为 `qt/publish-release.ps1`，仍交付签名的 `97Launch.exe`：Qt 运行库、MSVC CRT 和 .NET worker 压缩内嵌，首次运行展开到独立缓存。后台上传、校验及发布接口保持不变。Qt 客户端通过原 EXE 路径更新整包，并兼容原 WinUI 更新会话的启动确认。原 WinUI 发布脚本保留，后续 Qt 发布不应调用仓库根目录的旧构建入口。

应用更新与整合包共用现有授权后端、管理员会话、存储配额和上下行限速。更新检查与下载在设备配对前也可访问；管理员发布接口需要现有后台登录。

## 发布步骤

1. 在 Windows PowerShell 7 中运行 `./publish-release.ps1 -BuildNumber 1 -Channel preview -Restart`。后续发布使用更高的全局构建编号。
2. 工具生成 `artifacts/releases/<版本>-build.<编号>/97Launch.exe` 与 `manifest.json`。
3. 在后台「应用更新」选择这两个文件并填写更新说明。支持分片上传、暂停与续传。
4. 上传完成后，服务器校验数字签名、文件大小、SHA-256 与 PE 架构。状态为「待发布」时才能发布。
5. 发布后，客户端对应通道可检查、下载更新。撤回会停止新下载和正在进行的下载，不会强制降级已经安装的客户端。

同一构建编号、同一架构只能创建一次；内容不可覆盖。上传未完成时应继续原任务；取消或过期后重新发布需要新编号。撤回的已校验版本可以重新发布，但不能低于当前已发布构建。

## 签名与密钥

- 使用独立 RSA 3072 位发布密钥，SHA-256 + PKCS#1 v1.5 签名。
- 私钥默认路径为当前 Windows 用户目录下 `.97launch-private/update-signing-private.pem`；只在本机发布时使用，必须单独备份，禁止上传服务器或提交 Git。
- `update-signing-public.pem` 是公开验签密钥，服务器与应用使用同一份公钥。
- `manifest.json` 的 `payload` 是 Base64 编码的原始 UTF-8 JSON，签名覆盖这些原始字节。它绑定应用名、版本、构建编号、通道、架构、文件大小、SHA-256 与签发时间。
- HTTPS 继续使用现有严格证书验证；更新链接限制为授权服务的同源更新路径。
- 此签名用于应用更新校验，并非 Windows Authenticode 代码签名。Windows 首次运行仍可能显示来源提示。
- 更换发布公钥需要先向旧客户端分发包含新公钥的过渡版本；不要直接替换后端公钥。

## 通道与安装

- `preview`：接收测试版与稳定版中构建编号最高的发布。
- `stable`：只接收稳定版；禁止发布带预发布后缀的版本。
- 当前交付 x64 单 EXE。更新按整数构建编号判断，发布范围为 `1..65535`，本地开发编号为 `0`。
- 已配对客户端将更新放入传输队列；未配对客户端可在欢迎页「检查更新」中下载。
- 下载到本机更新缓存，支持断点续传；签名、哈希、架构与 EXE 实际版本全部通过后，等待用户点击「重启并更新」。
- 存在运行中的游戏或未完成传输时禁止应用更新。更新工具复制到临时会话目录，等待主进程退出后备份和替换原 EXE。
- 新版完成窗口初始化后确认启动；未确认或进程提前退出时恢复上一版。此检查只确认应用启动，不代表全部功能正常。
- 游戏目录、账户凭据、个人设置与启动历史不参与 EXE 替换。

## 接口

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/api/v1/updates/check?channel=preview&architecture=x64&build=0` | 获取最新发布与是否可更新 |
| GET | `/api/v1/updates/{id}` | 重新确认发布仍然有效 |
| GET / HEAD | `/api/v1/updates/{id}/download` | 下载 EXE，支持 Range、If-Range、ETag |
| GET | `/api/admin/releases` | 发布列表、下一构建编号、公钥标识 |
| POST | `/api/admin/releases` | 使用 `{manifest, notes}` 创建上传 |
| GET | `/api/admin/releases/{id}` | 查询状态与续传偏移量 |
| PUT | `/api/admin/releases/{id}/chunk?offset=N` | 上传不超过 1 MiB 的原始分片 |
| POST | `/api/admin/releases/{id}/complete` | 完成上传并校验 |
| POST | `/api/admin/releases/{id}/publish` | 发布已校验版本 |
| POST | `/api/admin/releases/{id}/withdraw` | 撤回版本 |
| DELETE | `/api/admin/releases/{id}` | 取消空闲的未完成上传 |

管理员变更请求需要有效管理员会话和同源 `Origin`。单个 EXE 上限 512 MiB，最多 8 个待上传任务，空闲超过 24 小时的未完成上传会清理。文件保存到现有服务数据目录的 `updates/`，不会存入源码发布目录。部署时保留此目录与 SQLite 数据库，并使用一份公钥。

反向代理配置见 `nginx-https.conf.example`；关闭更新下载的代理缓冲，上传采用 1 MiB 分片。服务仍以单 worker 运行，以保证共享限速与并发槽生效。
