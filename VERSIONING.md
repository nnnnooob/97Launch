# 版本与构建编号

当前基线：**0.1.0-alpha.1**。统一在 `Directory.Build.props` 中维护。

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

Git 标签使用完整版本并加 `v`，例如 `v0.1.0-alpha.1`。已发布的版本与标签不覆盖；有变化就递增版本。
后端服务与启动器独立版本化，后端自己的 API 版本不随本次启动器版本重置。

## 构建编号

- `BuildNumber` 是发布构建的递增整数，所有发布共用一个序列。
- `0` 仅表示本地开发构建，不进入自动更新发布序列。
- 正式发布流水线提供编号；同一构建重新上传时保留编号，重新构建并发布时使用新编号。
- 编号须为 `1..65535` 的整数，以适配 Windows 文件版本的第四段。
- 构建元数据写入 `InformationalVersion` 和 `AssemblyMetadata(BuildNumber)`；Windows 文件版本为 `0.1.0.<编号>`。
- 应用当前短版本显示会去除 `+` 后的构建元数据；日志和未来更新界面可读取独立编号。
- 未来更新器按发布通道、系统架构与构建编号判断更新，不使用字符串排序，也不把 SemVer 的 `+build.N` 当作版本优先级。

Debug 显示 `v0.1.0-alpha.1-dev`；Release 显示 `v0.1.0-alpha.1`。

## 发布构建示例

```powershell
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:VersionSuffix=alpha.1 -p:BuildNumber=1 -o artifacts/publish/win-x64
```

稳定版通过命令行显式传空后缀：`-p:VersionSuffix=`。
示例编号 `1` 仅作说明；实际发布必须选择尚未使用的递增编号。

本次建立源码版本管理，尚未接入自动更新器，也未发布安装包或 GitHub Release。
