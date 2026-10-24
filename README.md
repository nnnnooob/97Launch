# 97Launch

一个面向 Windows 的 Minecraft: Java 版第三方启动器。

## 这是什么

97Launch 是一个轻量的 Minecraft: Java 版启动器，使用 WinUI 3（C# / .NET 8）开发，
采用 Windows 11 原生的 Fluent 界面。它的目标是把「启动游戏」这件事做得足够简单：
打开就能看到当前实例，点一下就能进游戏，不需要在层层菜单里翻找。

本项目为个人小范围使用，不属于商业产品，免费分发、无广告。

## 关于本仓库

本仓库保存启动器、授权服务、管理后台及 NoneBot 插件源码。
版本与构建编号规则见 [VERSIONING.md](VERSIONING.md)。

游戏实例、下载缓存、账号凭据、服务端密钥和本地部署记录不纳入版本管理。

## 开发与构建

使用 Windows、.NET 8 SDK，以及支持 WinUI 3 的 Visual Studio 构建工具。

```powershell
dotnet restore src/Launcher.App/Launcher.App.csproj -p:Platform=x64
dotnet build src/Launcher.App/Launcher.App.csproj -p:Platform=x64
```

项目采用非 MSIX 部署方式。运行构建目录中的 `Launcher.App.exe`。
构建前按 `authorization-server.example.json` 在仓库根目录创建本地 `authorization-server.json`，填写实际服务地址；该文件已被 Git 忽略，会随构建复制到程序目录。配置和证书验证说明见 [客户端接入文档](server/auth/LAUNCHER.md)。
授权服务的部署与配置见 [server/auth/README.md](server/auth/README.md)，
服务器目录与整合包接口见 [server/auth/CATALOG.md](server/auth/CATALOG.md)。

## 主要功能

- **实例管理** — 为每个整合包 / 版本建立独立实例，互不干扰
- **账号管理** — 支持微软账号（正版）、离线账号与第三方验证服务器
- **版本下载** — 从官方源获取游戏本体与加载器
- **启动记录** — 持久化记录实例启动情况，运行日志保存在本机供排查
- **服务器列表** — 获取关联实例并启动加入服务器
- **皮肤预览** — 显示账号皮肤模型，支持行走动画与拖动旋转
- **原生界面** — WinUI 3 实现，跟随系统主题与 DPI

## 为什么要用微软账号登录

Minecraft: Java 版自 2021 年起全面迁移至 Microsoft 账号。启动器需要通过
Microsoft OAuth 完成登录，才能取得玩家自己的游戏档案。

97Launch 使用 **OAuth 2.0 设备代码流**（device code flow）：

1. 启动器向微软请求一个一次性登录代码
2. 浏览器中打开微软官方页面，用户自己输入代码并登录
3. 启动器轮询获取令牌

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
