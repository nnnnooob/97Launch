# 版本与构建编号

当前发布：**0.1.0-alpha.4 · 构建 4（Qt）**，默认测试版通道。Qt 版本由 `qt/CMakeLists.txt` 与发布脚本维护；原 WinUI 版本仍在 `Directory.Build.props` 中维护。

## 显示版本

采用 `主版本.次版本.修订版本`，开发期使用 `0.x.y`。

| 情况 | 示例 |
| --- | --- |
| 同一阶段的内部验收 | `0.1.0-alpha.1` → `0.1.0-alpha.2` |
| 功能基本完成，集中测试 | `0.1.0-beta.1` |
| 发布前候选 | `0.1.0-rc.1` |
| 稳定发布 | `0.1.0` |
| 稳定版修复 | `0.1.1` |
| 增加一轮新功能 | `0.2.0` |

Git 发布标签使用完整版本与构建编号，例如 `v0.1.0-alpha.4+build.4`。已有 `v0.1.0-alpha.1` 为原源码基线，保留原指向。已发布的版本与标签不覆盖；有变化就递增构建编号，下一轮预发布同时递增为 `alpha.5`。
后端服务与启动器独立版本化，后端自己的 API 版本不随本次启动器版本重置。

## 构建编号

- `BuildNumber` 是发布构建的递增整数，所有发布共用一个序列。
- `0` 仅表示本地开发构建，不进入自动更新发布序列。
- 正式发布流水线提供编号；同一构建重新上传时保留编号，重新构建并发布时使用新编号。
- 编号须为 `1..65535` 的整数，以适配 Windows 文件版本的第四段。
- 构建元数据写入 `InformationalVersion` 和 `AssemblyMetadata(BuildNumber)`；Windows 文件版本为 `0.1.0.<编号>`。
- 应用短版本显示会去除 `+` 后的构建元数据；更新界面单独显示构建编号。
- 更新器按发布通道、系统架构与构建编号判断更新，不使用字符串排序，也不把 SemVer 的 `+build.N` 当作版本优先级。

Debug 显示 `v0.1.0-alpha.2-dev`；Release 显示 `v0.1.0-alpha.2`。

## 发布构建示例

```powershell
./publish-release.ps1 -BuildNumber 2 -Channel preview -Restart
```

发布稳定版前，在 `Directory.Build.props` 清空 `VersionSuffix`，再使用 `-Channel stable` 生成签名清单。
构建编号 `2` 已用于本次发布；后续发布必须选择尚未使用的递增编号。

发布工具需要 PowerShell 7 和本机签名私钥。产物为根目录单文件 `97Launch.exe`，以及 `artifacts/releases/<版本>-build.<编号>` 中用于后台上传的 EXE 与签名清单。构建编号 `0` 不进入正式发布序列。

发布、撤回与接口说明见 [应用更新文档](server/auth/UPDATES.md)。GitHub 保存源码与发布标签，应用更新包由自有后端分发。
