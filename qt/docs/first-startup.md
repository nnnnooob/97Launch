# 首次进入与 WebView2

对照 WinUI `App.UpdateAuthorizedWindow`、`StartupInitializationView.RunAsync`：设备授权通过并处理启动更新后，Qt 在原欢迎布局内执行四个真实步骤。

1. 检查本地数据：创建数据/日志目录，确认配置和账号文件可读，并探测数据目录可写。损坏的已有文件保持原样。
2. 扫描实例：等待初始扫描或重新扫描已登记文件夹，统计文件夹和实例数量。目录提示保留在实例页和日志中。
3. 检索 Java：等待现有 Java 检索器完成，复用 WinUI 的递归搜索、附加目录和托管运行库规则；未找到 Java 仍可继续，启动游戏时按需下载。
4. 准备头像与登录环境：沿用 Qt 现有头像显示，读取当前皮肤图片，失败使用内置兼容头像。缺少 WebView2 时自动下载安装，失败可重试，也可稍后安装后进入。

全部完成后，原子保存 `%LOCALAPPDATA%/97LaunchQt/startup-v1.json`（schema 1）；下次跳过首次准备。不复用 WinUI 的初始化标记，以便迁移 Qt 后进行一次实际准备。标记无法保存时记录日志，并在下次启动重新准备；不更改授权凭据。授权失效时回到配对页，迟到的扫描/安装结果不能打开主界面。

WebView2 复用现有 worker，通过微软官方引导程序 `https://go.microsoft.com/fwlink/p/?LinkId=2124703` 下载。限制 HTTPS / 微软域名、最多八次重定向、10 MiB 文件上限与超时。执行前通过 Windows Authenticode 验证信任链、吊销状态和 Microsoft Corporation 签发者，然后使用 `/silent /install`。不请求提升权限；按微软的规则由当前进程权限决定用户级或计算机级安装。安装后检查注册表与 WebView2 Loader 的真实可用版本，不以退出码单独认定成功。

后续微软登录也会检查并补装 Runtime；取消登录只停止该登录，不强制终止已运行的系统组件安装器。Qt 离线/第三方账号和其他功能可在稍后安装后继续使用。上述流程自 Qt alpha.4 / 构建 4 起提供。

官方说明：https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
