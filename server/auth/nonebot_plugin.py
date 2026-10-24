"""97Launch 配对 Token 插件：NoneBot2 + OneBot v11，群号等配置见 nonebot.env.example。"""
import asyncio
import ipaddress
import re
import ssl
import time
import uuid
from urllib.parse import urlsplit

import httpx
from nonebot import get_plugin_config, logger, on_message
from nonebot.adapters import Event
from nonebot.adapters.onebot.v11 import Bot, GroupMessageEvent, MessageSegment
from nonebot.plugin import PluginMetadata
from pydantic import BaseModel, Field, SecretStr


class Config(BaseModel):
    launch_auth_url: str = "http://127.0.0.1:18797"
    launch_auth_ca_file: str = ""
    launch_auth_bot_key: SecretStr = SecretStr("")
    launch_auth_groups: set[int] = Field(default_factory=set)
    launch_auth_cooldown: int = Field(default=30, ge=5, le=300)


config = get_plugin_config(Config)

__plugin_meta__ = PluginMetadata(
    name="97Launch 配对 Token",
    description="在指定 QQ 群申请，通过私聊接收 97Launch 一次性配对 Token",
    usage="在允许的群聊中发送：@机器人 获取配对Token（也支持获取配对码），Token 私聊发送",
    type="application",
    config=Config,
    supported_adapters={"~onebot.v11"},
)


def api_endpoint() -> str:
    url = config.launch_auth_url.strip().rstrip("/")
    try:
        parts = urlsplit(url)
        parts.port  # Reject malformed ports before creating an HTTP request.
    except ValueError:
        raise ValueError("LAUNCH_AUTH_URL 地址或端口格式无效") from None
    try:
        loopback = ipaddress.ip_address(parts.hostname or "").is_loopback
    except ValueError:
        loopback = False
    valid = bool(parts.hostname and not parts.username and not parts.password and
                 not parts.query and not parts.fragment and
                 (parts.scheme == "https" or (parts.scheme == "http" and loopback)))
    if not valid:
        raise ValueError("LAUNCH_AUTH_URL 必须为 HTTPS 地址，或 SSH 通道/同机服务的 HTTP 回环地址")
    return url + "/api/v1/bot/pairing-codes"


endpoint = api_endpoint()
# The configured CA applies only to this client's configured service endpoint.
# Standard HTTPS uses system trust unless an administrator explicitly supplies a CA.
tls_context = ssl.create_default_context(cafile=config.launch_auth_ca_file.strip() or None)
if len(config.launch_auth_groups) > 100 or any(not re.fullmatch(r"\d{5,20}", str(g)) for g in config.launch_auth_groups):
    raise ValueError("LAUNCH_AUTH_GROUPS 请填写最多 100 个有效群号")
if not config.launch_auth_groups:
    logger.warning("97Launch 配对插件未配置 LAUNCH_AUTH_GROUPS，当前不响应任何群聊")
if not config.launch_auth_bot_key.get_secret_value().strip():
    logger.warning("97Launch 配对插件未配置 LAUNCH_AUTH_BOT_KEY，暂时无法申请配对 Token")


async def allowed_request(event: Event) -> bool:
    return (isinstance(event, GroupMessageEvent) and
            event.group_id in config.launch_auth_groups and
            event.sub_type != "anonymous" and event.is_tome() and
            event.get_plaintext().strip().casefold().replace(" ", "") in {"获取配对码", "获取配对token"})


pairing = on_message(rule=allowed_request, priority=10, block=True)
pending: set[int] = set()
last_request: dict[int, float] = {}

ERROR_MESSAGES = {
    "group_denied": "此群尚未在授权后台开放配对，请联系管理员。",
    "user_denied": "你尚未获得配对权限，请联系管理员。",
    "device_limit": "已达到设备数量上限，请先让管理员撤销旧设备。",
    "rate_limited": "请求过于频繁，请稍后再试。",
}


async def reply_to(event: GroupMessageEvent, text: str):
    await pairing.finish(MessageSegment.at(event.user_id) + MessageSegment.text("\n" + text))


async def revoke_delivery(pairing_id: uuid.UUID, event: GroupMessageEvent, key: str) -> bool:
    # Revocation is idempotent; retry only this exact record, never issue a replacement here.
    async with httpx.AsyncClient(timeout=8, follow_redirects=False, trust_env=False, verify=tls_context) as client:
        for _ in range(2):
            try:
                response = await client.post(f"{endpoint}/{pairing_id}/revoke",
                    headers={"Authorization": "Bearer " + key},
                    json={"qq_id": str(event.user_id), "group_id": str(event.group_id)})
                if response.is_success and response.json().get("revoked") is True:
                    return True
                if response.status_code < 500:
                    break
            except (httpx.HTTPError, ValueError, AttributeError):
                pass
    logger.warning("97Launch 私聊失败后的 Token 作废未确认，请检查授权服务；配对记录 {}", pairing_id)
    return False


async def deliver_token(bot: Bot, event: GroupMessageEvent, data: dict, key: str) -> str:
    pairing_id = uuid.UUID(data.get("pairing_id", ""))
    code, ttl = data.get("code"), data.get("expires_in")
    if not isinstance(code, str) or not re.fullmatch(r"97p_[A-Za-z0-9_-]{43}", code):
        raise ValueError("invalid pairing token response")
    if type(ttl) is not int or not 0 < ttl <= 3600:
        raise ValueError("invalid expiry response")
    duration = f"{ttl // 60} 分钟" if ttl % 60 == 0 else f"{ttl} 秒"
    private_messages = (
        "97Launch 配对 Token：",
        code,
        f"申请群：{event.group_id}\n"
        f"{duration}内在 97Launch 粘贴，仅可使用一次。请完整复制，区分大小写。\n"
        "此 Token 用于将设备绑定到你的 QQ，请勿转发。",
    )

    async def send_parts():
        for message in private_messages:
            result = await bot.send_private_msg(user_id=event.user_id, message=message, auto_escape=True)
            if not isinstance(result, dict) or type(result.get("message_id")) is not int:
                raise ValueError("invalid private message response")

    try:
        await asyncio.wait_for(send_parts(), timeout=20)
    except Exception as error:
        # Adapter exceptions may include the message payload: log the type only.
        logger.warning("97Launch 配对 Token 私聊发送失败（{}）", type(error).__name__)
        if await revoke_delivery(pairing_id, event, key):
            return "私聊发送失败，本次 Token 已作废。请添加机器人好友后重新申请。"
        return "私聊发送失败，暂未确认 Token 已作废。请勿使用本次收到的 Token，稍后重新申请；如未收到，请先添加机器人好友。"
    return f"配对 Token 已私聊发送，请在 {duration}内使用；请勿转发。"


@pairing.handle()
async def handle(bot: Bot, event: GroupMessageEvent):
    key = config.launch_auth_bot_key.get_secret_value().strip()
    if not key:
        await reply_to(event, "配对服务尚未配置，请联系管理员。")
        return
    if event.user_id in pending:
        await reply_to(event, "正在获取配对 Token，请稍候，不要重复申请。")
        return
    now = time.monotonic()
    for user_id, requested_at in list(last_request.items()):
        if now - requested_at >= config.launch_auth_cooldown:
            del last_request[user_id]
    if event.user_id in last_request:
        remaining = max(1, int(config.launch_auth_cooldown - (now - last_request[event.user_id]) + 0.999))
        await reply_to(event, f"请在 {remaining} 秒后再申请；新 Token 会让旧 Token 失效。")
        return

    last_request[event.user_id] = now
    pending.add(event.user_id)
    reply = "授权服务暂时不可用，请稍后再试。"
    try:
        nickname = event.sender.card or event.sender.nickname or ""
        async with httpx.AsyncClient(timeout=12, follow_redirects=False, trust_env=False, verify=tls_context) as client:
            response = await client.post(endpoint,
                headers={"Authorization": "Bearer " + key},
                json={"qq_id": str(event.user_id), "group_id": str(event.group_id), "nickname": nickname[:64]})
        # The request is never automatically retried: issuing again invalidates the previous code.
        if response.is_success:
            data = response.json()
            if not isinstance(data, dict):
                raise ValueError("invalid pairing response")
            reply = await deliver_token(bot, event, data, key)
        elif response.status_code == 401:
            reply = "机器人接口密钥配置有误，请联系管理员。"
            logger.warning("97Launch 配对接口拒绝机器人鉴权（HTTP 401）")
        elif response.status_code in (403, 409, 429):
            data = response.json()
            error = data.get("error", {}) if isinstance(data, dict) else {}
            code = error.get("code") if isinstance(error, dict) else None
            if not isinstance(code, str):
                code = None
            reply = ERROR_MESSAGES.get(code, "请求未被授权服务允许，请联系管理员。")
            if response.status_code == 429:
                reply = ERROR_MESSAGES["rate_limited"]
        else:
            logger.warning("97Launch 配对请求未完成（HTTP {}）", response.status_code)
    except (httpx.HTTPError, ValueError, TypeError, AttributeError) as error:
        # Never log response bodies, pairing codes, Authorization headers or exception URLs.
        logger.warning("97Launch 配对请求未完成（{}）", type(error).__name__)
    finally:
        pending.discard(event.user_id)
    await reply_to(event, reply)
