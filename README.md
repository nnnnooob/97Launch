# 97Launch

一个面向 Windows 的 Minecraft: Java 版第三方启动器。

## 这是什么

97Launch 是一个轻量的 Minecraft: Java 版启动器。当前 alpha.5 使用 Qt Quick / C++ 界面，
配合自包含的 .NET 8 游戏与下载任务进程。原 WinUI 3 源码保留在 `src/`。它的目标是把「启动游戏」这件事做得足够简单：
打开就能看到当前实例，点一下就能进游戏，不需要在层层菜单里翻找。

本项目为个人小范围使用，不属于商业产品，免费分发、无广告。

## 关于本仓库

本仓库保存启动器、授权服务、管理后台及 NoneBot 插件源码。
版本与构建编号规则见 [VERSIONING.md](VERSIONING.md)。

游戏实例、下载缓存、账号凭据、服务端密钥和本地部署记录不纳入版本管理。

## 开发与构建

当前 Qt 版源码位于 [`qt/`](qt/)，需要 Windows、Visual Studio C++ 构建工具、.NET 8 SDK 和 Qt 6.6.3 MSVC x64（Qt Quick、Qt Image Formats）。

先按 `authorization-server.example.json` 在仓库根目录创建本地 `authorization-server.json`，填写实际服务地址。该文件由 Git 忽略，发布时会随单文件打包。

```powershell
cd qt
./build.ps1 -Run -QtSdk 'C:/Qt/6.6.3/msvc2019_64' -AuthorizationConfigPath '../authorization-server.json'
```

开发构建生成 `qt/97LaunchQt.exe` 和配套运行文件。对外发布使用：

```powershell
./publish-release.ps1 -BuildNumber 5 -Channel preview -QtSdk 'C:/Qt/6.6.3/msvc2019_64' -AuthorizationConfigPath '../authorization-server.json'
```

产物为 `qt/artifacts/releases/0.1.0-alpha.5-build.5/97Launch.exe` 与签名 `manifest.json`。用户只需一个 EXE，Qt / MSVC / .NET 任务端首次运行自动展开到 `%LOCALAPPDATA%/97LaunchQt/runtime/<包哈希>/`，后续复用。内置游戏目录仍为 EXE 同级 `.minecraft`，账号和设置独立保存。替换此 EXE 即更新整套运行文件；旧版缓存保留以便回滚。Qt 的动态链接库可在缓存目录替换，第三方许可随包提供。微软登录使用系统 WebView2 Runtime，缺失时自动从微软下载、验签并静默安装。

旧 WinUI 版仍使用仓库根目录的 `./build.ps1 -Restart`，不要与 Qt 构建入口混用。

授权配置见 [客户端接入文档](server/auth/LAUNCHER.md)，后端部署见 [server/auth/README.md](server/auth/README.md)，服务器与整合包接口见 [server/auth/CATALOG.md](server/auth/CATALOG.md)，版本发布见 [应用更新文档](server/auth/UPDATES.md)。

## 主要功能

- **实例管理** — 为每个整合包 / 版本建立独立实例，互不干扰
- **账号管理** — 支持微软账号（正版）、离线账号与第三方验证服务器
- **版本下载** — 从官方源获取游戏本体与加载器
- **启动记录** — 持久化记录实例启动情况，运行日志保存在本机供排查
- **服务器列表** — 获取关联实例并启动加入服务器
- **账号头像** — 显示账号皮肤头像，支持账号切换
- **应用更新** — 支持检查更新、签名验证、断点续传与重启替换，启动失败时尝试恢复上一版
- **界面** — Qt Quick 灰白界面，共用弹窗、提示及页面切换动效

## 为什么要用微软账号登录

Minecraft: Java 版自 2021 年起全面迁移至 Microsoft 账号。启动器需要通过
Microsoft OAuth 完成登录，才能取得玩家自己的游戏档案。

Qt 版使用 **OAuth 2.0 授权码 + PKCE**：

1. 按需打开系统 WebView2 中的微软官方授权页面
2. 用户在微软页面完成登录，应用接收授权码并验证 state
3. 使用 PKCE 换取令牌，继续 Xbox Live、XSTS 与 Minecraft 授权

**整个过程中，登录完全发生在微软官方页面上，启动器不会、也无法接触到用户的账号密码。**

## 我们用 Minecraft 服务 API 做什么

登录成功后，启动器会调用 Minecraft: Java 版游戏服务 API 完成三件事：

| 用途 | 说明 |
|---|---|
| 获取游戏档案 | 读取玩家自己的用户名与 UUID，用于界面显示与启动游戏时传参 |
| 获取皮肤 | 读取玩家自己的皮肤地址，在启动器中显示正确的头像 |
| 检查游戏所有权 | 确认该账号确实拥有 Minecraft: Java 版，避免无权限账号启动失败 |

## 我们不做什么

- 不收集、不存储用户的微软账号密码
- 不将微软游戏账号凭据上传至启动器授权服务器
- 不包含统计上报、广告或任何形式的追踪
- 不修改、不重新分发 Minecraft 游戏文件
- 不用于任何商业目的

所有账号令牌使用 Windows DPAPI 加密后保存在用户本机。

小范围使用的设备配对通过自有授权服务器完成。配对记录包含申请人的 QQ、群号、昵称、设备名称、平台、启动器版本及最近连接时间/IP；此授权与 Minecraft 游戏账号分开，设备凭据使用 Windows DPAPI 加密保存在本机。

## 系统要求

- Windows 10 版本 2004（内部版本 19041）或更高
- x64 架构

## 联系

如有任何关于 API 使用方式的疑问，欢迎通过仓库 Issue 联系。

---

*Minecraft 是 Mojang Studios 的商标。本项目与 Mojang Studios、Microsoft 无隶属关系。*
