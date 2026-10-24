# 97Launch 配对 Token 插件

适用于 **NoneBot2（2.2+）+ OneBot v11**。使用专用 CA 的远程 HTTPS 部署需配置 CA 公钥文件。其他适配器需要另做适配。

## 安装

在机器人使用的 Python 环境安装依赖（已经安装的 NoneBot 与适配器不必重复安装）：

```sh
python -m pip install "nonebot2>=2.2,<3" nonebot-adapter-onebot "httpx>=0.27,<1"
```

将 `nonebot_plugin.py` 复制到机器人的插件目录，改名为 `nonebot_plugin_97launch.py`，使用专用 CA 时，将 `97launch-auth-ca.pem` 保存在本地，并通过 `LAUNCH_AUTH_CA_FILE` 指定路径。

如果项目已经用 `nonebot.load_plugins("plugins")` 加载插件目录，放到该目录即可。否则在机器人入口的 `nonebot.init()`、注册 OneBot v11 适配器之后，加入：

```python
nonebot.load_plugin("plugins.nonebot_plugin_97launch")
```

上面的路径对应项目根目录下 `plugins/nonebot_plugin_97launch.py`。只选一种加载方式，避免重复加载。

## 自定义群聊

把 `nonebot.env.example` 内容加入机器人实际使用的 `.env.prod` 或 `.env.dev`，例如：

```dotenv
LAUNCH_AUTH_GROUPS=[123456789,987654321]
LAUNCH_AUTH_URL=http://127.0.0.1:18797
LAUNCH_AUTH_BOT_KEY=填入实际的机器人密钥
LAUNCH_AUTH_COOLDOWN=30
```

- `LAUNCH_AUTH_GROUPS`：允许使用的群号，JSON 数组。单群写 `[123456789]`，多个群用逗号分隔。留空列表 `[]` 时所有群都不响应。修改配置后重启机器人。
- `LAUNCH_AUTH_URL`：授权后端根地址，**不带** `/api/v1/bot/pairing-codes`。
- `LAUNCH_AUTH_BOT_KEY`：`%USERPROFILE%\.97launch-private\server-access.json` 中的 `bot_key`。不要填 `admin_key` 或服务器密码，不要把密钥发送到群聊。
- `LAUNCH_AUTH_COOLDOWN`：同一 QQ 请求间隔，默认 30 秒。请求失败也计入间隔，避免反复请求刷屏。

**后端管理页 → 接入设置 → 允许的 QQ 群，也要填写相同的群号并保存。** 插件白名单限制机器人响应哪些群；后台白名单决定是否允许签发配对 Token，两个条件都必须满足。无需把管理密钥交给机器人。

### 地址怎么填

| 机器人的运行位置 | 地址 |
| --- | --- |
| 与授权服务同机 | `http://127.0.0.1:18797` |
| 在已建立管理 SSH 通道的这台 Windows 电脑 | `http://127.0.0.1:19797`，通道必须保持运行 |
| 其他远程服务器 | `https://auth.example.com:18443`（示例），按证书配置 CA |
| Docker 容器中 | 容器回环地址不是宿主机，需单独配置容器到后端的受保护网络或可用 HTTPS |

`auth.example.com` 仅为占位地址。远程机器人将 `LAUNCH_AUTH_URL` 改为实际 HTTPS 根地址；使用专用 CA 时，额外设置 `LAUNCH_AUTH_CA_FILE=/path/to/97launch-auth-ca.pem`。插件只向配置的服务请求，继续检查主机名/IP、有效期和证书链；未配置 CA 文件时使用系统信任。同机机器人可使用回环地址。插件不接受远程明文 HTTP、不跟随重定向、不使用系统代理。

## 使用

在允许的群聊中发送：

```text
@机器人 获取配对 Token
```

机器人请求授权服务，收到成功响应后，使用消息事件中的申请人 QQ 号**私聊发送 Token**。群里只 @ 申请人并提示“配对 Token 已私聊发送”，不会展示 Token。有效期读取服务端响应，当前为 5 分钟。

私聊按顺序分三条发送：第一条为 `97Launch 配对 Token：`，第二条仅包含完整 Token，第三条包含申请群、有效期和使用说明，便于直接复制第二条。每条均检查发送结果，三条整体最多等待 20 秒；全部发送成功后才在群内报告成功。

请先添加机器人好友，确保其可以向你发送私聊。非好友能否通过临时会话收到消息取决于实际 QQ 协议端与会话权限，插件不假定一定可用。私聊接口成功代表协议端接受发送，不保证用户已经阅读。

任意一条私聊失败或整体 20 秒超时时，插件调用后端撤销**本次 pairing_id 对应的 Token**；作废成功后在群里提示添加好友并重新申请，不会回退到群里公开 Token。作废请求最多尝试两次，均未确认成功时会明确提示“暂未确认 Token 已作废”，后台可人工作废；Token 仍受 5 分钟有效期限制。若部分消息或超时消息实际上已送达，该 Token 也应视为作废，以群内提示为准。

返回的 Token 形如 `97p_<43位随机字符>`，共 47 个字符；请完整复制并保留大小写、下划线与连字符。这个占位字符串不是真实 Token。

支持 `@机器人 获取配对Token`，也保留 `@机器人 获取配对码`。本版需要后端 **1.2.0+**，其中包含机器人作废接口；替换插件文件并重启机器人，群号与密钥配置不变。旧的群回复版插件必须替换，否则它仍会在群里发 Token。

未开放的群、私聊、匿名消息及不匹配的内容不响应。匹配遵循 OneBot 的“与机器人有关”状态，通常由 @ 触发；如果配置了机器人昵称或使用回复，适配器也可能视为与机器人有关。

同一 QQ 正在请求时不会重复发起；新申请会使旧码失效，因此插件不自动重试。QQ 和群号来自消息事件，不接受用户在命令中指定他人 QQ。

## 常见提示

- **此群尚未在授权后台开放配对**：在管理网页放行群号，不能只改插件配置。
- **机器人接口密钥配置有误**：检查 `bot_key`，修改后重启机器人。
- **授权服务暂时不可用**：检查机器人所在机器是否能连接配置的地址，SSH 通道是否还在，以及公网 HTTPS 是否已完成。
- **已达到设备数量上限**：在管理页撤销旧设备后重新获取配对 Token。
- **私聊发送失败，本次 Token 已作废**：添加机器人好友并确认协议端可以发送私聊，再回到允许的群里重新申请。
- **暂未确认 Token 已作废**：检查后端版本、网络与机器人密钥，在后台按申请人/申请时间找到本次记录人工作废；不要使用可能已送达的那条 Token。
- 完全没有响应：确认适配器为 OneBot v11、插件已加载、群号在配置中，并且消息 @ 了机器人。检查启动日志中的未配置提示。

本次交付为插件文件与配置说明，未连接实际机器人或发出 QQ 消息，未执行测试。

参考：[NoneBot 配置文档](https://nonebot.dev/docs/appendices/config)、[OneBot v11 事件文档](https://onebot.adapters.nonebot.dev/docs/api/v11/event/)、[私聊消息接口](https://onebot.adapters.nonebot.dev/docs/api/v11/bot/#async-method-send_private_msg-user_id-message-auto_escape)。
