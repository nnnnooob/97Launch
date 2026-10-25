# 下载与传输迁移

来源：`97Launch/src/Launcher.App` 中的 `DownloadViewModel`、`DownloadPage`、
`DownloadInstallDialog`、`ResourceDownloadDialog`、`TransferViewModel` 和
`TransferPage`，以及现有 Core/Services 源码。迁移日期：2026-10-25。

## 下载

- Minecraft：最新两次正式发布及最新预览，正式、预览、远古、愚人节分类。
- 安装：目标文件夹、实例命名、Forge/NeoForge/Fabric/LiteLoader/OptiFine，
  Fabric API 和 OptiFabric 的自动选择、手动清除与兼容约束。
- Mod、整合包、光影、资源包、数据包：双平台搜索、来源/分类/排序/版本/
  加载器/分辨率筛选，250 ms 防抖与每源每页 50 项的分页。
- 按当前文件夹提供实例安装目标；数据包选择存档并检查世界锁。
- 详情：按游戏和加载器分组、正式版筛选、发布版本选择、必要前置浏览、
  已安装状态、项目主页、复制名称/链接、按原文件命名设置另存为。
- 前置详情最多 16 层；返回保留父级发布选择、筛选和滚动位置。
- Mod 替换确认、重复文件阻止、前置与冲突检查、旧文件备份使用原服务。
- 在线/本地整合包使用原安装器，保留安装标记、恢复、覆盖文件和隔离规则。
  CurseForge 内容缺失需用户确认后继续。

## 传输

- 与实例修改/资源操作共用 FIFO 写入队列。
- 原阶段权重、并行阶段进度、速率、等待/完成/失败统计。
- 单任务取消、失败重试、移除记录、清除已结束任务、导出文件定位。
- 离开传输页清除终态记录，与原 `OnNavigatedFrom` 行为一致。
- 安装完成刷新实例，并按 `SelectNewInstance` 选择结果、返回启动页。
- 有记录时左栏为 224 px；无记录时收至 0，右侧显示“暂无任务”。
- 下载详情共用实例设置的标题淡入淡出和返回入口；进入详情将侧栏收至 0。
- 实例列表、下载版本分组及资源发布分组共用 `LiteListGroupHeader` /
  `LiteListGroupRow`；最新版本固定展开。侧栏六个图标统一 24 px 线框规范。

## 实现入口

- `qml/components/LiteDownloadPage.qml`、`LiteDownloadDetail.qml`、
  `LiteDownloadSidebar.qml`；安装选择复用 `LiteInstanceModify.qml`。
- `qml/components/LiteTransferPage.qml`、`LiteTransferSummary.qml`。
- `src/instancejobs.*`：队列、瞬时进程、确认问答、重试数据、进度聚合。
- `backend/DownloadActions.cs`：目录与兼容性校验、社区搜索与安装分派。
- `backend/Services/ModrinthModpackInstaller.cs`、`LocalModpackService.cs`：
  原业务源码；前者仅将 WinUI 确认窗口替换为宿主问答。

构建使用项目 `build.ps1`。未执行实际下载安装或修改现有游戏文件的测试。
