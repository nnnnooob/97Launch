# 单 EXE 发布

`publish-release.ps1` 构建 Qt 界面与自包含 .NET worker，用 `windeployqt` 收集动态运行库，并加入 MSVC CRT、WebP 插件、第三方许可与本机授权服务配置。`makecab` 生成 LZX 压缩载荷，独立 Win32 启动器将载荷嵌入 RCDATA，不依赖用户预先安装 Qt 或 .NET。

启动器按嵌入载荷的 SHA-256 选择 `%LOCALAPPDATA%/97LaunchQt/runtime/<hash>`。互斥锁保护首次解压，所有文件在临时目录展开后再原子重命名，`.complete` 表示完整缓存；后续启动复用此缓存。包内路径拒绝绝对路径、冒号和上级跳转。

`LAUNCH_PORTABLE_EXE` 将原始 EXE 路径传给界面及 worker，游戏目录、版本信息和导出都以它为准；worker 与 Qt 资源仍从缓存加载。`LAUNCH_PORTABLE_PID` 指向等待界面退出的外层进程，更新器验证并等待该进程退出后替换原 EXE。旧缓存不覆盖，失败时可重新启动上一版本。接受原 WinUI 更新目录中的合法启动确认会话，支持 alpha.2 到 Qt alpha.3 的迁移。

签名清单仍使用现有 format 1、RSA/SHA-256 和递增全局构建号，后台无需改变上传格式。实际运行前验证大小、SHA-256、签名、PE 架构与文件版本。发布私钥不进入运行包；授权部署配置只打入运行包，不进入源码。

修改 Qt DLL 或插件时可直接替换对应缓存文件并保留 `.complete`；上游源码和许可见 `licenses/Qt/NOTICE.txt`。
