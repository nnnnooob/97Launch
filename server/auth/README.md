# 97Launch 配对授权后端

独立于 Minecraft 正版登录。群内向 NoneBot 申请，Token 私聊发送给申请人，启动器兑换设备凭据；群内只显示发送结果。

## 部署配置

本文使用 `auth.example.com` 作为占位地址，不包含实际部署 IP。真实地址、凭据、数据库与部署记录保留在服务器或 Git 忽略的本地配置中。

- `AUTH_ORIGIN`：管理员配置的公网 HTTPS 根地址。部署时显式传入，例如 `AUTH_ORIGIN=https://auth.example.com:18443 ./deploy.sh`，写入 `/etc/97launch-auth/deployment.env`。
- `AUTH_PUBLIC_READY=true`：在公网 HTTPS 配置完成后启用。
- API 默认仅监听 `127.0.0.1:18797`，由 Nginx 代理。`nginx-https.conf.example` 的占位主机名需替换为实际地址。
- 使用专用 CA 的 IP HTTPS 部署：`AUTH_PUBLIC_IP=<实际 IPv4> ./configure-ip-https.sh`；地址保留在服务器 TLS 配置中供续签使用。
- 源码部署于 `/opt/97launch-auth/releases/<version>`，`current` 链接指向当前版本。
- 数据库：`/var/lib/97launch-auth/auth.sqlite3`；哈希密钥：`/etc/97launch-auth/secrets.json`。
- 首次部署生成的原始管理/机器人密钥：`/root/97launch-auth-access.json`，仅 root 可读。不要提交这些文件。
- 数据库每日备份到 `/var/backups/97launch-auth`，保留 14 天。恢复时同时保留数据库和哈希密钥。
- 操作记录、配对历史保留 90 天；设备记录持续保留。

## 管理页面

访问管理员配置的公网 HTTPS 根地址。使用专用 CA 时，浏览器需预先信任该 CA 公钥证书。也可自行建立 SSH 回环转发使用本地管理入口；转发端口需与 `AUTH_LOCAL_ADMIN_ORIGIN` 一致。

管理密钥登录，无用户名。会话 8 小时，HttpOnly / Secure / SameSite=Strict Cookie；不把管理密钥存入浏览器存储。`admin_key` 只用于后台，`bot_key` 只用于机器人接口。

1. 在「接入设置」填写允许的群号；初始为空，不允许申请配对 Token。
2. 可以再限制 QQ 号；QQ 白名单为空表示允许上述群所有成员。
3. 每个 QQ 默认最多 3 台有效设备，可改为 1–20 台。
4. 配对记录只展示掩码，完整码仅在申请时返回；设备凭据也仅兑换时返回。
5. 撤销设备后，下次联网验证失效。改白名单影响新申请和未兑换码，不自动撤销旧设备。

## API 约定

JSON 请求/响应，QQ 和群号均为**字符串**。时间均为 UTC Unix 秒。请求体最大 16 KiB。错误格式：

```json
{"error":{"code":"code_invalid","message":"配对 Token 无效、已使用或已过期"}}
```

HTTP：400 无效配对 Token，401 凭据无效，403 权限/撤销，409 设备数量/绑定冲突，422 参数无效，429 限流（带 Retry-After）。未知服务器错误不要显示给群成员。

### 机器人：申请配对 Token

`POST /api/v1/bot/pairing-codes`

Header：`Authorization: Bearer <bot_key>`，密钥仅放 NoneBot 服务端环境。

```json
{"qq_id":"123456789","group_id":"987654321","nickname":"玩家"}
```

响应结构（以下只表示字段，不是可用码）：

```json
{"pairing_id":"uuid","code":"97p_<43位随机字符>","expires_in":300,"expires_at":1790000000,"single_use":true}
```

同一 QQ 再次获取会作废旧的未使用码。每 QQ 每分钟 3 次、每群每分钟 60 次、机器人鉴权每来源 IP 每分钟 120 次。QQ/群身份由持有机器人密钥的可信机器人提供，不支持启动器自行传 QQ 签发。

### Token 格式（2026-10-23 更新）

配对凭据现为 `97p_` + 32 随机字节的 Base64URL 编码，总长 **47 个字符**，区分大小写。随机部分包含大小写字母、数字、下划线和连字符。继续使用 5 分钟有效期和单次兑换；数据库仅保存 HMAC 校验值及掩码。

接口路径及 JSON 字段名 `code` 保留，字段内容已改为长 Token。兑换时只忽略复制附带的空白字符，**不转大写、不去掉连字符或下划线**。旧 8 位配对凭据不再接受；已授权设备的 `97d_` 长期凭据继续有效。

部署新后端时请一并更新 `nonebot_plugin.py`。机器人支持 `@机器人 获取配对Token` 和原来的 `@机器人 获取配对码`。旧插件只识别短码格式，不能直接用于新版后端。

本文 JSON 示例中的 `97p_<43位随机字符>` 是字段占位说明，不是真实可用 Token。

### 机器人：私聊失败后作废 Token（1.2.0+）

`POST /api/v1/bot/pairing-codes/{pairing_id}/revoke`，使用相同的机器人 Bearer 密钥。

请求体 `{"qq_id":"123456789","group_id":"987654321"}`。QQ 与群号必须同时匹配该签发记录；只作废指定记录，不会作废后来重新申请的 Token。重复作废返回 `{"revoked":true}`，已兑换返回 409，不会撤销已授权设备。不存在或归属不匹配返回 404。成功作废写入 `code_delivery_failed` 操作记录。

插件在私聊失败/超时后调用此接口，最多尝试两次。群里不展示 Token；若无法确认作废，会明确提示服务异常，而不是谎报已经作废。网络恢复后管理员可从配对记录手工作废；未作废的 Token 仍会在 5 分钟后过期。

### 启动器：兑换

`POST /api/v1/pairing/redeem`，不需要机器人密钥。

```json
{"code":"97p_<43位随机字符>","installation_id":"b7dd4ac8-f6d4-46ba-b217-51ddc3f25c30","device_name":"我的电脑","platform":"windows","app_version":"0.0.1-dev"}
```

`installation_id`：客户端首次安装随机生成 UUID 并持久化；不是硬件指纹。每来源 IP 每 5 分钟最多 10 次兑换尝试。

成功返回 `device_token`（Bearer）、`token_type`、`expires_at:null` 以及 `device`。凭据长期有效，直到撤销/主动退出/重新配对该安装。SQLite 事务保证一次兑换；同一安装重新配对会替换旧凭据。响应丢失时需要重新获取配对 Token。

客户端应立即用 Windows DPAPI 保存设备凭据，避免日志记录、明文设置及群消息。管理与机器人密钥不进入客户端。

### 启动器：验证与退出

- `GET /api/v1/devices/me`：Bearer 设备凭据；返回 `authorized:true` 与设备详情。
- `POST /api/v1/devices/heartbeat`：同一 Bearer，JSON `{"app_version":"0.0.1-dev"}`。更新最近连接时间与 IP，返回 `server_time`、`next_check_in:900`。
- `POST /api/v1/devices/logout`：撤销本机凭据，之后客户端删除本地凭据。
- 有效设备接口按 IP 每分钟共 90 次。撤销返回 `device_revoked`，凭据不正确返回 `device_unauthorized`。

服务端暂不签发离线许可；离线放行时长、客户端防重复登录界面，在启动器接入时再确定。不要把网络异常当作凭据失效，也不要把已明确撤销当作离线异常放行。

### 管理 API

同源 Cookie 会话；写操作校验 Origin。`GET /api/admin/openapi.json` 在管理登录后提供完整接口定义。

- `POST /api/admin/login`：`{"key":"<admin_key>"}`
- `POST /api/admin/logout`
- `GET /api/admin/overview`
- `GET /api/admin/devices?q=&offset=0`
- `GET /api/admin/pairing-codes?offset=0`
- `GET /api/admin/audit?offset=0`
- `PUT /api/admin/policy`：`allowed_group_ids`、`allowed_qq_ids`、`max_devices_per_user`
- `POST /api/admin/devices/{uuid}/revoke`
- `POST /api/admin/pairing-codes/{uuid}/revoke`

列表每页 50 条，返回 items/total/offset/limit。

## NoneBot 接入

`nonebot_plugin.py` 适用于 **NoneBot2 + OneBot v11**，支持配置群白名单、请求间隔及重复请求保护。远程 IP HTTPS 需一并放置 CA 公钥文件。完整安装方式见 [NONEBOT.md](NONEBOT.md)，配置模板见 [nonebot.env.example](nonebot.env.example)。若实际用 QQ 官方适配器，需要替换事件/群回复部分。后端不依赖具体适配器。

### 管理员获取配对 Token

管理页 → 配对记录 → 获取 Token：填写所属 QQ、选择已开放的群，可填写昵称。无须调用机器人接口或提供机器人密钥。生成的 Token 仍为五分钟、单次兑换，遵循群/QQ 白名单、设备数量上限和签发频率限制；同 QQ 先前未兑换的 Token 会作废。

`POST /api/admin/pairing-codes` 使用管理会话 Cookie 和同源校验，Body 与机器人签发接口相同。完整 Token 只返回一次，数据库仅保存摘要与尾号，操作记录标识为“管理员签发 Token”。页面不持久化 Token，关闭、管理会话失效或到期时清空。

机器人依赖 `httpx`，环境设置：

```dotenv
LAUNCH_AUTH_GROUPS=[123456789,987654321]
LAUNCH_AUTH_URL=http://127.0.0.1:18797
LAUNCH_AUTH_BOT_KEY=<部署时生成的 bot_key>
LAUNCH_AUTH_COOLDOWN=30
```

上述地址只用于机器人与后端在同一宿主机。机器人在本机 Windows 且 SSH 通道运行时，用 `http://127.0.0.1:19797`；其他远程部署需先完成公网 HTTPS。插件与后台管理页都必须放行相同的群号。加载后，在允许的群发 `@机器人 获取配对 Token`。插件收到后发送 HTTP 请求，等待响应，再私聊申请人发送 Token，群里仅显示发送结果；私聊失败时作废本次 Token 并提示添加机器人好友。接口本身不主动发 QQ 消息。QQ/群号来自事件，兑换后自动关联设备；主动转发 Token 仍可能把他人设备绑定到申请人名下。

## 运维

```sh
systemctl status 97launch-auth
journalctl -u 97launch-auth --since today
systemctl restart 97launch-auth
systemctl list-timers '97launch*'
curl --fail http://127.0.0.1:18797/healthz
```

服务使用独立系统用户，单进程、内存上限 512 MiB，API 不直接监听公网，不记录请求体和 access log。服务端只存配对 Token/设备凭据的 HMAC；原始机器人/管理密钥只留在 root 的交付文件。请勿公开或提交该文件。

这是自用规模的单服务器实现。机器人持有签发权；主机 root 可以访问授权数据。启动器原生欢迎页与设备授权已接入，详见 [LAUNCHER.md](LAUNCHER.md)；实际机器人配置与真实配对仍待联调，未实现组织权限系统或跨服务器数据库。未运行正式测试，部署后仅检查启动、静态页与健康接口。
## 1.4.0 服务器与整合包管理

已在原后台增加服务器目录、整合包版本文件库、共享上传下载限速和 MCSManager 连接。操作说明、客户端接口、限速边界及文件备份范围见 [CATALOG.md](CATALOG.md)。
