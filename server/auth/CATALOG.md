# 服务器目录与整合包文件库（1.4.0）

后台入口为管理员配置的 HTTPS 根地址，例如 `https://auth.example.com:18443/`（占位示例）。沿用原管理密钥及 8 小时管理会话。

## 管理员使用

1. **带宽与连接**：配置 MCSM 源地址和 API Key。当前同机连接 `http://127.0.0.1:23333`，API Key 不回显、不下发给启动器。远程 MCSM 连接要求 HTTPS。
2. **整合包**：新建包名及版本，上传客户端 CurseForge ZIP 或 Modrinth MRPACK。根目录必须有对应清单；服务端不解压执行整合包。上传完成后计算整个文件的 SHA-256，解析游戏与加载器版本。
3. **服务器**：同步 MCSM，配置玩家使用的公网地址及端口、显示名称、简介、关联整合包版本、排序及可见群。手动勾选发布后，已授权启动器才会读到它。

关联整合包仅供玩家“获取相关实例”。启动服务器时仍使用启动器主页当前选中的本地实例。

### 版本与续传

- 每个版本对应独立文件，不能覆盖或复用同一版本号。更新后需在服务器配置中选择新版本。
- 已关联的版本不能归档；解除关联后可以归档，随时恢复。归档保留原始文件并继续计入配额。
- 上传按 1 MiB 分片、串行续传。断线、刷新或重新登录后，在“待完成上传”中重新选择原文件即可续传。
- 续传校验大小及文件首尾指纹；入库后以完整文件 SHA-256 为准。不能把后端生成的 SHA-256 当作客户端上传前自带的校验值。
- 上传暂停后，24 小时未续传会自动过期并移除临时文件。校验失败的临时文件也保留至取消或过期，计入预留配额。
- 当前文件格式限于启动器已有的 CurseForge / Modrinth 导入流程；不把任意 ZIP 当成可安装实例。

## 客户端接口

所有 `/api/v1/servers*` 和 `/api/v1/packs*` 请求都要求 `Authorization: Bearer <device_token>`。完整定义见登录后的 `/api/admin/openapi.json`。

| 接口 | 用途 |
| --- | --- |
| `GET /api/v1/servers` | 获取当前设备可见的已发布服务器，默认隐藏已停止/停止中的实例 |
| `GET /api/v1/servers?include_offline=true` | 包括停止状态，供后续可选离线筛选使用 |
| `GET /api/v1/servers/{id}` | 重新确认单个服务器、连接地址和关联包状态 |
| `GET /api/v1/servers/{id}/icon` | 同源 PNG；获取不到服务器图标时返回 Minecraft 原版默认服务器图标 |
| `POST /api/v1/servers/{id}/pack` | 取得对应版本元数据和一次下载会话凭证，无请求体 |
| `GET /api/v1/packs/{version_id}/download` | 下载文件；同时要求设备凭据及 `X-Download-Lease` |
| `HEAD /api/v1/packs/{version_id}/download` | 读取长度、ETag、SHA-256 及 Range 支持 |

服务器字段包括 `id/name/description/host/port/address/state/players/max_players/minecraft/loader/motd/icon_url/pack/can_join/has_related_instance/order`。不返回 MCSM 密钥、运行命令、RCON、内部检测地址或其他游戏实例。

### 获取并安装相关实例

`POST …/pack` 返回：

```json
{
  "id": "<version_id>",
  "filename": "client.mrpack",
  "size": 123456,
  "sha256": "<64 hex>",
  "format": "modrinth",
  "download_url": "/api/v1/packs/<version_id>/download",
  "download_lease": "<download credential>",
  "lease_expires_at": 0,
  "install_action": "transfer_then_homepage",
  "auto_launch": false
}
```

- `download_lease` 只放在请求头，不拼到 URL；与设备和文件版本绑定，有效 24 小时。设备撤销后不能继续发起下载。
- 新发布版本、归档或解除旧关联不影响已取得且未过期的下载凭证；文件地址与内容保持不变。
- 客户端须以完整 SHA-256 和大小校验落盘文件后，再交给现有整合包安装流程，避免直接安装不完整文件。
- 支持单区间 `Range: bytes=start-end`，206 / 416、`Accept-Ranges`、以 SHA-256 为 ETag 的 `If-Range`。
- 下载并发已满返回 429，附 `Retry-After: 15`；客户端应等待并重试，不应把它当成文件永久失效。
- 未关联时返回 404 / `no_related_instance`，界面显示“暂无相关实例”。
- 安装进入传输页，成功返回启动页；不自动启动、不替玩家切换当前选中实例。

### 状态

仅同步 MCSM `config.type == minecraft/java`。不能用 `pingConfig.type` 筛选游戏类型，它在终端和其他游戏实例上也可能保留默认值 1。

状态为 `online/starting/stopping/stopped/unreachable/unknown/maintenance`。进程运行时再使用 Minecraft Java 状态协议探测游戏端口，取得人数、版本、MOTD、favicon。`online` 才有 `can_join=true`。这些是后端探测结果，玩家网络延迟应由客户端另测。

后台每 30 秒同步。面板断开、节点不可用、记录过期或实例消失时标为未知，不冒充停止。维护状态可见但禁止加入。所有新发现的条目默认未发布。

## 带宽和运行约束

初始保守设置：总下载 2 MiB/s、每个 QQ 合计 1 MiB/s、最多 3 个下载；总上传 2 MiB/s、单任务 1 MiB/s、最多 1 个上传分片连接。单个 QQ 最多同时下载 2 个连接，不能靠分段多线程绕开 QQ 级速度限制。

上限单位为应用有效载荷 MiB/s，1 MiB/s 约等于 8.39 Mbps，不等于网卡线速。应用共享字节调度器限制下载总量和每个 QQ 的合计速度，上传共享调度器配合 Nginx `proxy_request_buffering off` 与小分片，通过 TCP 反压控制持续接收速度；系统与网络缓冲会产生有限的短时突发。它不是全机网络 QoS，也不能保证其他程序/攻击流量不占带宽。按服务器套餐实际上下行调整并给游戏预留余量。

**必须只运行一个 Uvicorn worker**，共享限速与并发计数在同一进程；多 worker 需先改为跨进程共享调度。当前 systemd 已保持单 worker。配置变更实时作用于后续数据块，新并发限制不强制中断已在传输的连接。

## 数据与备份

- SQLite：`/var/lib/97launch-auth/auth.sqlite3`，增加 `packs/pack_versions/pack_uploads/servers/download_leases` 表及 `traffic` 设置；保留原授权数据。
- 文件：`/var/lib/97launch-auth/packs/{blobs,staging,icons}`，文件路径使用内部 UUID，下载入口不接受任意文件系统路径。
- MCSM 凭据：`/var/lib/97launch-auth/mcsm.json`，权限 600，仅运行服务用户及 root 可读。
- 发布目录与文件库分离，升级服务不覆盖文件；服务退出再启动可恢复上传进度和服务器关联。
- 既有每日任务继续备份 SQLite。**该备份不包含大体积整合包原文件**；恢复文件库需同步备份 `packs/blobs` 和 `mcsm.json`，不要只恢复数据库。
- 未发布前可保存配置，MCSM 临时离线时仍可修改已登记服务器、取消发布。

MCSM 接入依据官方 [实例 API](https://docs.mcsmanager.com/zh_cn/apis/api_instance.html)、[节点 API](https://docs.mcsmanager.com/apis/api_daemon.html) 和 [API Key 文档](https://docs.mcsmanager.com/zh_cn/apis/get_apikey.html)。
