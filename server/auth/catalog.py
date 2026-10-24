"""Server publishing and immutable pack storage, hosted by the authorization service."""
from __future__ import annotations

import asyncio
import base64
import hashlib
import ipaddress
import json
import os
import re
import shutil
import struct
import time
import uuid
import zipfile
from collections import Counter
from contextlib import suppress
from pathlib import Path, PurePosixPath
from urllib.parse import quote, urlsplit

import httpx
from anyio import fail_after
from fastapi import Depends, Query, Request
from fastapi.responses import Response, StreamingResponse
from pydantic import BaseModel, ConfigDict, Field, field_validator
from starlette.background import BackgroundTask

MIB = 1024 * 1024
GIB = 1024 * MIB
CHUNK = MIB
BLOCK = 32 * 1024
ID = r"^[0-9a-f]{32}$"


class Body(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


class Traffic(Body):
    download_total_mib: float = Field(default=2, ge=.0625, le=1024)
    download_user_mib: float = Field(default=1, ge=.0625, le=1024)
    upload_total_mib: float = Field(default=2, ge=.0625, le=1024)
    upload_task_mib: float = Field(default=1, ge=.0625, le=1024)
    download_slots: int = Field(default=3, ge=1, le=32)
    upload_slots: int = Field(default=1, ge=1, le=4)
    max_file_gib: int = Field(default=8, ge=1, le=32)
    storage_gib: int = Field(default=50, ge=1, le=1000)


class Connection(Body):
    base_url: str = Field(default="http://127.0.0.1:23333", max_length=250)
    api_key: str = Field(default="", max_length=256)

    @field_validator("base_url")
    @classmethod
    def validate_url(cls, value):
        u = urlsplit(value)
        if (u.scheme not in ("http", "https") or not u.hostname or u.username or u.password or
                u.query or u.fragment or u.path not in ("", "/")):
            raise ValueError("MCSM 地址必须为完整的 HTTP(S) 源地址")
        if u.scheme == "http" and u.hostname not in ("127.0.0.1", "localhost", "::1"):
            raise ValueError("远程 MCSM 必须使用 HTTPS；同机可使用回环 HTTP")
        _ = u.port
        return value.rstrip("/")


class Upload(Body):
    pack_id: uuid.UUID | None = None
    name: str = Field(min_length=1, max_length=80)
    version: str = Field(min_length=1, max_length=60)
    filename: str = Field(min_length=1, max_length=180)
    size: int = Field(gt=0, le=32 * GIB)
    fingerprint: str = Field(pattern=r"^[0-9a-f]{64}$")

    @field_validator("filename")
    @classmethod
    def validate_filename(cls, value):
        if any(c in value for c in '/\\\r\n\0') or not value.lower().endswith((".zip", ".mrpack")):
            raise ValueError("仅支持 ZIP / MRPACK 整合包")
        return value


class PackEdit(Body):
    name: str = Field(min_length=1, max_length=80)


def hostname(value):
    value = value.strip().strip("[]")
    if not value or len(value) > 253:
        raise ValueError("服务器地址无效")
    try:
        ipaddress.ip_address(value)
    except ValueError:
        if not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", value):
            raise ValueError("仅填写 IP 或域名，不含协议和端口")
        if any(not part or len(part) > 63 for part in value.split(".")):
            raise ValueError("域名无效")
    return value.lower()


class ServerEdit(Body):
    daemon_id: str = Field(pattern=ID)
    instance_id: str = Field(pattern=ID)
    name: str = Field(min_length=1, max_length=80)
    description: str = Field(default="", max_length=250)
    host: str = Field(min_length=1, max_length=253)
    port: int = Field(default=25565, ge=1, le=65535)
    probe_host: str = Field(default="127.0.0.1", min_length=1, max_length=253)
    probe_port: int = Field(default=25565, ge=1, le=65535)
    minecraft: str = Field(default="", max_length=64)
    loader: str = Field(default="", max_length=64)
    version_id: uuid.UUID | None = None
    order: int = Field(default=0, ge=-10000, le=10000)
    published: bool = False
    maintenance: bool = False
    group_ids: list[str] = Field(default_factory=list, max_length=100)

    _host = field_validator("host", "probe_host")(hostname)

    @field_validator("group_ids")
    @classmethod
    def groups(cls, value):
        if any(not re.fullmatch(r"\d{5,20}", v) for v in value):
            raise ValueError("群号应为 5–20 位数字")
        return list(dict.fromkeys(value))


class Pacer:
    """One worker shares the same byte schedule across every concurrent stream."""
    def __init__(self):
        self.next = 0.0
        self.lock = asyncio.Lock()

    async def wait(self, size, speed):
        async with self.lock:
            now = time.monotonic()
            self.next = max(now, self.next) + size / speed
            delay = self.next - now
        await asyncio.sleep(delay)


def varint(value):
    value &= 0xffffffff
    result = bytearray()
    while True:
        byte = value & 127
        value >>= 7
        result.append(byte | (128 if value else 0))
        if not value:
            return bytes(result)


async def read_varint(reader):
    result = 0
    for i in range(5):
        value = (await reader.readexactly(1))[0]
        result |= (value & 127) << (7 * i)
        if not value & 128:
            return result
    raise ValueError("Invalid Minecraft packet")


def plain_motd(value):
    if isinstance(value, str):
        return re.sub(r"§.", "", value)
    if isinstance(value, list):
        return "".join(plain_motd(v) for v in value)
    if isinstance(value, dict):
        return plain_motd(value.get("text", "")) + plain_motd(value.get("extra", []))
    return ""


async def minecraft_status(host, port):
    writer = None
    try:
        with fail_after(4):
            reader, writer = await asyncio.open_connection(host, port, limit=192 * 1024)
            encoded = host.encode("utf-8")
            handshake = b"\0" + varint(-1) + varint(len(encoded)) + encoded + struct.pack(">H", port) + b"\1"
            writer.write(varint(len(handshake)) + handshake + b"\1\0")
            await writer.drain()
            length = await read_varint(reader)
            if not 2 <= length <= 160 * 1024:
                raise ValueError("Status packet too large")
            if await read_varint(reader) != 0:
                raise ValueError("Invalid status packet")
            size = await read_varint(reader)
            if not 1 <= size <= length - 2:
                raise ValueError("Invalid status length")
            value = json.loads(await reader.readexactly(size))
            players = value.get("players", {})
            icon = None
            favicon = value.get("favicon", "")
            if isinstance(favicon, str) and favicon.startswith("data:image/png;base64,") and len(favicon) < 150000:
                with suppress(ValueError, struct.error):
                    png = base64.b64decode(favicon.split(",", 1)[1], validate=True)
                    if png[:16] == b"\x89PNG\r\n\x1a\n\0\0\0\rIHDR" and struct.unpack(">II", png[16:24]) == (64, 64):
                        icon = png
            return {"online": True, "players": max(0, int(players.get("online", 0))),
                    "max_players": max(0, int(players.get("max", 0))),
                    "version": str(value.get("version", {}).get("name", ""))[:100],
                    "motd": plain_motd(value.get("description", ""))[:500], "icon": icon}
    except (OSError, ValueError, TypeError, AttributeError, TimeoutError, asyncio.TimeoutError, asyncio.IncompleteReadError):
        return {"online": False}
    finally:
        if writer:
            writer.close()
            with suppress(OSError, asyncio.TimeoutError):
                await asyncio.wait_for(writer.wait_closed(), 1)


def inspect_pack(path):
    """Read bounded metadata only; uploaded archives are never extracted on the host."""
    sha = hashlib.sha256()
    with path.open("rb") as f:
        while block := f.read(MIB):
            sha.update(block)
    with path.open("rb") as f:
        f.seek(max(0, path.stat().st_size - 65557))
        tail = f.read()
        pos = tail.rfind(b"PK\x05\x06")
        if pos < 0 or len(tail) - pos < 22:
            raise ValueError("文件不是完整的 ZIP 整合包")
        if struct.unpack_from("<I", tail, pos + 12)[0] > 8 * MIB:
            raise ValueError("整合包目录过大")
    try:
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            if len(entries) > 20000 or sum(e.file_size for e in entries) > 64 * GIB:
                raise ValueError("整合包解压体积或文件数量超出限制")
            for e in entries:
                p = PurePosixPath(e.filename.replace("\\", "/"))
                if p.is_absolute() or ".." in p.parts or ":" in e.filename or e.flag_bits & 1 or (e.external_attr >> 16) & 0o170000 == 0o120000:
                    raise ValueError("压缩包包含不安全路径、符号链接或加密文件")
            name = "modrinth.index.json" if "modrinth.index.json" in archive.namelist() else "manifest.json"
            if archive.getinfo(name).file_size > 4 * MIB:
                raise ValueError("整合包清单过大")
            data = json.loads(archive.read(name))
            if name == "modrinth.index.json":
                if data.get("game") != "minecraft" or data.get("formatVersion") != 1 or not isinstance(data.get("files"), list):
                    raise ValueError("Modrinth 整合包清单不完整")
                deps = data.get("dependencies", {})
                mc = str(deps.get("minecraft", ""))[:64]
                loader = next((k + " " + str(deps[k]) for k in ("neoforge", "forge", "fabric-loader", "quilt-loader") if k in deps), "原版")[:64]
                kind = "modrinth"
            else:
                if data.get("manifestType") != "minecraftModpack" or not isinstance(data.get("files"), list):
                    raise ValueError("CurseForge 整合包清单不完整")
                mc = str(data.get("minecraft", {}).get("version", ""))[:64]
                loaders = data.get("minecraft", {}).get("modLoaders", [])
                loader = str(next((v.get("id", "") for v in loaders if v.get("primary")), loaders[0].get("id", "") if loaders else "原版"))[:64]
                kind = "curseforge"
            if not mc:
                raise ValueError("整合包缺少 Minecraft 版本")
        return {"sha256": sha.hexdigest(), "format": kind, "minecraft": mc, "loader": loader}
    except (zipfile.BadZipFile, KeyError, json.JSONDecodeError, UnicodeError, TypeError, AttributeError) as e:
        raise ValueError("无法识别整合包；需要根目录含 manifest.json 或 modrinth.index.json 的客户端包") from e


class Catalog:
    def __init__(self, db, fail, audit, digest, root, db_path):
        self.db, self.fail, self.audit, self.digest, self.root = db, fail, audit, digest, root
        self.storage = Path(os.environ.get("AUTH_PACK_STORAGE", str(db_path.parent / "packs")))
        self.connection_path = db_path.parent / "mcsm.json"
        self.traffic = Traffic().model_dump()
        self.up_pacer, self.down_pacer = Pacer(), Pacer()
        self.user_pacers = {}
        self.upload_locks = set()
        self.finalizing = set()
        self.active = {}
        self.upload_active = 0
        self.download_users = Counter()
        self.bytes_up = self.bytes_down = 0
        self.inventory = {}
        self.statuses = {}
        self.sync_lock = asyncio.Lock()
        self.sync_at = 0
        self.sync_error = "尚未同步 MCSM"
        self.task = None
        self.client = None
        self.started_at = int(time.time())

    async def start(self):
        for folder in ("blobs", "staging", "icons"):
            (self.storage / folder).mkdir(parents=True, exist_ok=True, mode=0o700)
        with self.db() as c:
            c.executescript("""
                CREATE TABLE IF NOT EXISTS packs(id TEXT PRIMARY KEY,name TEXT NOT NULL,created_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS pack_versions(
                    id TEXT PRIMARY KEY,pack_id TEXT NOT NULL REFERENCES packs(id),version TEXT NOT NULL,
                    filename TEXT NOT NULL,size INTEGER NOT NULL,sha256 TEXT NOT NULL,format TEXT NOT NULL,
                    minecraft TEXT NOT NULL,loader TEXT NOT NULL,created_at INTEGER NOT NULL,archived_at INTEGER,
                    UNIQUE(pack_id,version));
                CREATE TABLE IF NOT EXISTS pack_uploads(
                    id TEXT PRIMARY KEY,pack_id TEXT NOT NULL REFERENCES packs(id),version TEXT NOT NULL,
                    filename TEXT NOT NULL,size INTEGER NOT NULL,created_at INTEGER NOT NULL,updated_at INTEGER NOT NULL,
                    status TEXT NOT NULL DEFAULT 'uploading',error TEXT NOT NULL DEFAULT '',version_id TEXT,
                    fingerprint TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS servers(id TEXT PRIMARY KEY,config TEXT NOT NULL,updated_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS download_leases(
                    token_hash TEXT PRIMARY KEY,device_id TEXT NOT NULL,version_id TEXT NOT NULL,
                    expires_at INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS versions_pack ON pack_versions(pack_id,created_at);
            """)
            c.execute("INSERT OR IGNORE INTO settings VALUES('traffic',?)", (json.dumps(self.traffic),))
            self.traffic = Traffic(**json.loads(c.execute("SELECT value FROM settings WHERE key='traffic'").fetchone()[0])).model_dump()
            for row in c.execute("SELECT id FROM pack_uploads WHERE status='uploading'"):
                blob = self.storage / "blobs" / (row["id"] + ".pack")
                stage = self.storage / "staging" / (row["id"] + ".part")
                if blob.exists() and not stage.exists() and not c.execute("SELECT 1 FROM pack_versions WHERE id=?", (row["id"],)).fetchone():
                    blob.replace(stage)
        self.client = httpx.AsyncClient(timeout=httpx.Timeout(10, connect=4), follow_redirects=False,
                                        trust_env=False, limits=httpx.Limits(max_connections=4))
        self.task = asyncio.create_task(self.background())

    async def stop(self):
        if self.task:
            self.task.cancel()
            with suppress(asyncio.CancelledError):
                await self.task
        if self.client:
            await self.client.aclose()

    def connection(self):
        try:
            return Connection(**json.loads(self.connection_path.read_text())).model_dump()
        except FileNotFoundError:
            return Connection().model_dump()

    def write_connection(self, value):
        temp = self.connection_path.with_suffix(".tmp")
        temp.write_text(json.dumps(value))
        temp.chmod(0o600)
        temp.replace(self.connection_path)

    async def mcsm(self, path, **query):
        connection = self.connection()
        if not connection["api_key"]:
            self.fail(409, "mcsm_not_configured", "请先在带宽与连接中填写 MCSM API Key")
        try:
            async with self.client.stream("GET", connection["base_url"] + path,
                                          params={"apikey": connection["api_key"], **query},
                                          headers={"X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json; charset=utf-8"}) as r:
                if r.status_code != 200:
                    raise ValueError("HTTP status")
                body = bytearray()
                async for part in r.aiter_bytes():
                    body.extend(part)
                    if len(body) > 8 * MIB:
                        raise ValueError("Response too large")
                value = json.loads(body)
                if value.get("status") == 403:
                    self.fail(502, "mcsm_denied", "MCSM 拒绝访问，请检查 API Key 权限")
                if value.get("status") != 200:
                    raise ValueError("MCSM response")
                return value["data"]
        except (httpx.HTTPError, ValueError, KeyError, TypeError):
            # HTTP exceptions include the URL (and API key); never expose/log their repr.
            self.fail(502, "mcsm_unavailable", "无法连接 MCSM，请检查地址、密钥或节点状态")

    async def sync(self):
        async with self.sync_lock:
            now = int(time.time())
            try:
                with fail_after(45):
                    nodes = await self.mcsm("/api/service/remote_services")
                    inventory = {}
                    for node in nodes[:16]:
                        daemon = str(node.get("uuid", ""))
                        if not re.fullmatch(ID, daemon):
                            continue
                        items = node.get("instances", [])
                        for item in items[:500]:
                            config = item.get("config", {})
                            ping = config.get("pingConfig", {})
                            if config.get("type") != "minecraft/java":
                                continue
                            instance = str(item.get("instanceUuid", ""))
                            if not re.fullmatch(ID, instance):
                                continue
                            # Copy only public catalog fields, never commands/environment/RCON passwords.
                            inventory[f"{daemon}:{instance}"] = {
                                "daemon_id": daemon, "instance_id": instance,
                                "node": str(node.get("remarks", ""))[:80],
                                "name": str(config.get("nickname", "Minecraft"))[:80],
                                "process_status": item.get("status") if node.get("available") else None,
                                "probe_host": str(ping.get("ip") or "127.0.0.1")[:253],
                                "probe_port": int(ping.get("port") or 25565), "checked_at": now}
                    self.inventory = inventory
                    self.sync_at, self.sync_error = now, ""
            except Exception as e:
                self.sync_error = (e.detail.get("message", "MCSM 同步失败") if hasattr(e, "detail") and isinstance(e.detail, dict)
                                   else "MCSM 同步超时或返回格式异常")
            await self.probe()

    def configs(self):
        with self.db() as c:
            return {r["id"]: json.loads(r["config"]) for r in c.execute("SELECT * FROM servers")}

    async def probe(self):
        configs = self.configs()
        semaphore = asyncio.Semaphore(4)

        async def one(key, entry):
            cfg = next((v for v in configs.values() if key == f'{v["daemon_id"]}:{v["instance_id"]}'), None)
            if entry.get("process_status") != 3 or self.sync_error:
                self.statuses[key] = {"online": False, "checked_at": int(time.time())}
                return
            async with semaphore:
                result = await minecraft_status((cfg or entry)["probe_host"], (cfg or entry)["probe_port"])
            icon = result.pop("icon", None)
            if icon:
                path = self.storage / "icons" / (entry["instance_id"] + ".png")
                temp = path.with_suffix(".tmp")
                temp.write_bytes(icon)
                temp.replace(path)
            result["checked_at"] = int(time.time())
            self.statuses[key] = result

        # ponytail: cap status probes for this small private catalog; paginate/schedule separately beyond 100 instances.
        await asyncio.gather(*(one(k, v) for k, v in list(self.inventory.items())[:100]))

    async def background(self):
        while True:
            try:
                await self.sync()
                await self.cleanup()
            except asyncio.CancelledError:
                raise
            except Exception:
                self.sync_error = "服务器同步暂不可用，将自动重试"
            await asyncio.sleep(30)

    async def cleanup(self):
        now = int(time.time())
        with self.db() as c:
            c.execute("DELETE FROM download_leases WHERE expires_at<?", (now,))
            rows = c.execute("SELECT id FROM pack_uploads WHERE status IN ('uploading','failed') AND updated_at<?", (now - 86400,)).fetchall()
            for r in rows:
                if r["id"] in self.upload_locks or r["id"] in self.finalizing:
                    continue
                (self.storage / "staging" / (r["id"] + ".part")).unlink(missing_ok=True)
                c.execute("UPDATE pack_uploads SET status='expired',error='上传已过期' WHERE id=?", (r["id"],))
        for key, pacer in list(self.user_pacers.items()):
            if not self.download_users[key] and time.monotonic() - pacer.next > 300:
                self.user_pacers.pop(key, None)

    def usage(self):
        with self.db() as c:
            used = c.execute("SELECT COALESCE(SUM(size),0) FROM pack_versions").fetchone()[0]
            reserved = c.execute("SELECT COALESCE(SUM(size),0) FROM pack_uploads WHERE status IN ('uploading','failed')").fetchone()[0]
        return {"stored_bytes": used, "reserved_bytes": reserved,
                "free_disk_bytes": shutil.disk_usage(self.storage).free,
                "quota_bytes": self.traffic["storage_gib"] * GIB}

    def version(self, version_id):
        with self.db() as c:
            r = c.execute("SELECT v.*,p.name FROM pack_versions v JOIN packs p ON v.pack_id=p.id WHERE v.id=?", (str(version_id),)).fetchone()
        if not r:
            self.fail(404, "pack_missing", "整合包版本不存在")
        return dict(r)

    def render(self, server_id, cfg, admin=False):
        key = f'{cfg["daemon_id"]}:{cfg["instance_id"]}'
        source = self.inventory.get(key, {})
        status = self.statuses.get(key, {})
        stale = bool(self.sync_error or time.time() - self.sync_at > 90 or not source)
        process = source.get("process_status")
        if cfg.get("maintenance"):
            state = "maintenance"
        elif stale or process is None or process == -1:
            state = "unknown"
        elif process == 0:
            state = "stopped"
        elif process == 1:
            state = "stopping"
        elif process == 2:
            state = "starting"
        else:
            state = "online" if status.get("online") and time.time() - status.get("checked_at", 0) < 90 else "unreachable"
        pack = None
        if cfg.get("version_id"):
            with suppress(Exception):
                v = self.version(cfg["version_id"])
                if not v["archived_at"]:
                    pack = {k: v[k] for k in ("id", "name", "version", "size", "sha256", "format", "minecraft", "loader")}
        host = cfg["host"]
        address = f'[{host}]:{cfg["port"]}' if ":" in host else f'{host}:{cfg["port"]}'
        result = {"id": server_id, "name": cfg["name"], "description": cfg["description"],
                  "host": host, "port": cfg["port"], "address": address, "state": state,
                  "players": status.get("players") if state == "online" else None,
                  "max_players": status.get("max_players") if state == "online" else None,
                  "minecraft": cfg["minecraft"] or status.get("version", ""), "loader": cfg["loader"],
                  "motd": status.get("motd", ""), "checked_at": status.get("checked_at"),
                  "icon_url": f"/api/{'admin' if admin else 'v1'}/servers/{server_id}/icon",
                  "icon_source": "server" if (self.storage / "icons" / (cfg["instance_id"] + ".png")).exists() else "minecraft_default",
                  "pack": pack, "can_join": state == "online", "has_related_instance": pack is not None,
                  "order": cfg["order"]}
        if admin:
            result.update({"config": cfg, "managed": True, "published": cfg["published"],
                           "node": source.get("node", ""), "process_status": process})
        return result

    def visible(self, cfg, device):
        return cfg["published"] and (not cfg["group_ids"] or device["group_id"] in cfg["group_ids"])

    def require_server(self, server_id, device=None):
        cfg = self.configs().get(str(server_id))
        if not cfg or (device and not self.visible(cfg, device)):
            self.fail(404, "server_missing", "服务器不存在或未向此设备开放")
        return cfg

    def icon(self, cfg):
        path = self.storage / "icons" / (cfg["instance_id"] + ".png")
        if not path.exists():
            path = self.root / "static" / "default-server.png"
        return Response(path.read_bytes(), media_type="image/png")

    def upload_row(self, upload_id):
        with self.db() as c:
            r = c.execute("SELECT * FROM pack_uploads WHERE id=?", (str(upload_id),)).fetchone()
        if not r:
            self.fail(404, "upload_missing", "上传任务不存在")
        result = dict(r)
        path = self.storage / "staging" / (result["id"] + ".part")
        result["offset"] = path.stat().st_size if path.exists() else 0
        result["chunk_size"] = CHUNK
        return result

    async def download(self, request, version, owner, admin=False):
        path = self.storage / "blobs" / (version["id"] + ".pack")
        if not path.is_file() or path.stat().st_size != version["size"]:
            self.fail(503, "pack_unavailable", "整合包文件暂不可用，请联系管理员")
        etag = '"' + version["sha256"] + '"'
        start, end = 0, version["size"] - 1
        range_header = request.headers.get("range", "")
        if request.headers.get("if-range") not in (None, etag):
            range_header = ""
        code = 200
        if range_header:
            m = re.fullmatch(r"bytes=(\d*)-(\d*)", range_header)
            if not m or not any(m.groups()):
                return Response(status_code=416, headers={"Content-Range": f'bytes */{version["size"]}'})
            a, b = m.groups()
            if not a:
                start = max(0, version["size"] - int(b))
            else:
                start, end = int(a), min(int(b) if b else end, end)
            if start > end or start >= version["size"]:
                return Response(status_code=416, headers={"Content-Range": f'bytes */{version["size"]}'})
            code = 206
        headers = {"Content-Length": str(end - start + 1), "Accept-Ranges": "bytes", "ETag": etag,
                   "Content-Disposition": "attachment; filename*=UTF-8''" + quote(version["filename"], safe=""),
                   "X-Content-SHA256": version["sha256"], "X-Accel-Buffering": "no"}
        if code == 206:
            headers["Content-Range"] = f'bytes {start}-{end}/{version["size"]}'
        if request.method == "HEAD":
            return Response(status_code=code, headers=headers, media_type="application/octet-stream")
        if len([v for v in self.active.values() if v["direction"] == "download"]) >= self.traffic["download_slots"] or self.download_users[owner] >= 2:
            from fastapi import HTTPException
            raise HTTPException(429, detail={"code": "download_busy", "message": "下载通道繁忙，请稍后重试"}, headers={"Retry-After": "15"})
        transfer_id = str(uuid.uuid4())
        user = self.user_pacers.setdefault(owner, Pacer())
        self.download_users[owner] += 1
        self.active[transfer_id] = {"id": transfer_id, "direction": "download", "name": version["filename"],
                                    "owner": owner, "bytes": start, "size": version["size"], "started_at": int(time.time())}

        def release():
            if self.active.pop(transfer_id, None) is not None:
                self.download_users[owner] -= 1

        async def stream():
            try:
                with path.open("rb") as f:
                    f.seek(start)
                    remaining = end - start + 1
                    while remaining:
                        block = f.read(min(BLOCK, remaining))
                        if not block:
                            break
                        await asyncio.gather(self.down_pacer.wait(len(block), self.traffic["download_total_mib"] * MIB),
                                             user.wait(len(block), self.traffic["download_user_mib"] * MIB))
                        remaining -= len(block)
                        self.bytes_down += len(block)
                        self.active[transfer_id]["bytes"] += len(block)
                        yield block
            finally:
                release()
        return StreamingResponse(stream(), status_code=code, media_type="application/octet-stream", headers=headers,
                                 background=BackgroundTask(release))

    def install_routes(self, app, admin_auth, device_auth):
        admin = [Depends(admin_auth)]

        @app.get("/api/admin/catalog", dependencies=admin, tags=["catalog-admin"])
        def catalog_overview():
            with self.db() as c:
                pack_count = c.execute("SELECT COUNT(*) FROM packs").fetchone()[0]
                versions = c.execute("SELECT COUNT(*) FROM pack_versions WHERE archived_at IS NULL").fetchone()[0]
            configs = self.configs()
            return {"mcsm": {"configured": bool(self.connection()["api_key"]), "last_sync_at": self.sync_at,
                             "error": self.sync_error, "instances": len(self.inventory)},
                    "servers": len(configs), "published": sum(v["published"] for v in configs.values()),
                    "packs": pack_count, "versions": versions, "storage": self.usage()}

        @app.get("/api/admin/traffic", dependencies=admin, tags=["catalog-admin"])
        def traffic():
            return {"limits": self.traffic, "storage": self.usage(), "active": list(self.active.values()),
                    "bytes_uploaded": self.bytes_up, "bytes_downloaded": self.bytes_down, "since": self.started_at,
                    "connection": {"base_url": self.connection()["base_url"], "has_api_key": bool(self.connection()["api_key"])}}

        @app.put("/api/admin/traffic", dependencies=admin, tags=["catalog-admin"])
        async def save_traffic(body: Traffic, request: Request):
            value = body.model_dump()
            usage = self.usage()
            if value["storage_gib"] * GIB < usage["stored_bytes"] + usage["reserved_bytes"]:
                self.fail(409, "quota_in_use", "存储上限不能小于现有文件与上传预留空间")
            if value["download_user_mib"] > value["download_total_mib"] or value["upload_task_mib"] > value["upload_total_mib"]:
                self.fail(422, "invalid_limits", "单用户/单任务速度不能高于总速度")
            with self.db() as c:
                c.execute("UPDATE settings SET value=? WHERE key='traffic'", (json.dumps(value),))
                self.audit(c, request, "traffic_updated", detail=json.dumps(value))
            self.traffic = value
            return value

        @app.put("/api/admin/mcsm", dependencies=admin, tags=["catalog-admin"])
        async def save_connection(body: Connection, request: Request):
            value = body.model_dump()
            if not value["api_key"]:
                value["api_key"] = self.connection()["api_key"]
            if not value["api_key"]:
                self.fail(422, "key_required", "请填写 MCSM API Key")
            self.write_connection(value)
            with self.db() as c:
                self.audit(c, request, "mcsm_updated", detail=value["base_url"])
            await self.sync()
            return {"connected": not bool(self.sync_error), "error": self.sync_error, "instances": len(self.inventory)}

        @app.post("/api/admin/mcsm/sync", dependencies=admin, tags=["catalog-admin"])
        async def sync_now(request: Request):
            await self.sync()
            return {"connected": not bool(self.sync_error), "error": self.sync_error, "instances": len(self.inventory), "last_sync_at": self.sync_at}

        @app.get("/api/admin/servers", dependencies=admin, tags=["catalog-admin"])
        def servers():
            configs = self.configs()
            items = [self.render(k, v, True) for k, v in configs.items()]
            managed = {f'{v["daemon_id"]}:{v["instance_id"]}' for v in configs.values()}
            for key, v in self.inventory.items():
                if key not in managed:
                    items.append({**v, "id": None, "managed": False, "published": False,
                                  "state": "unknown" if self.sync_error else {0: "stopped", 1: "stopping", 2: "starting", 3: "online" if self.statuses.get(key, {}).get("online") else "unreachable"}.get(v["process_status"], "unknown"),
                                  "players": self.statuses.get(key, {}).get("players"), "max_players": self.statuses.get(key, {}).get("max_players"),
                                  "icon_url": "/static/default-server.png", "pack": None})
            return {"items": sorted(items, key=lambda v: (v.get("order", 0), v["name"])),
                    "sync_at": self.sync_at, "sync_error": self.sync_error}

        def save_server(server_id, body, request):
            value = body.model_dump(mode="json")
            key = f'{value["daemon_id"]}:{value["instance_id"]}'
            old = self.configs().get(server_id)
            known = old and key == f'{old["daemon_id"]}:{old["instance_id"]}'
            if key not in self.inventory and not known:
                self.fail(409, "instance_not_found", "MCSM 中未找到此 Minecraft 实例，请先同步")
            if value["version_id"] and self.version(value["version_id"])["archived_at"]:
                self.fail(409, "pack_archived", "已归档版本不能关联服务器")
            with self.db() as c:
                existing = c.execute("SELECT id,config FROM servers").fetchall()
                if any(r["id"] != server_id and f'{json.loads(r["config"])["daemon_id"]}:{json.loads(r["config"])["instance_id"]}' == key for r in existing):
                    self.fail(409, "already_managed", "此 MCSM 实例已经添加")
                c.execute("INSERT INTO servers VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET config=excluded.config,updated_at=excluded.updated_at",
                          (server_id, json.dumps(value, ensure_ascii=False), int(time.time())))
                self.audit(c, request, "server_updated", server_id, value["name"] + (" · 已发布" if value["published"] else " · 未发布"))
            return self.render(server_id, value, True)

        @app.post("/api/admin/servers", dependencies=admin, tags=["catalog-admin"])
        async def create_server(body: ServerEdit, request: Request):
            return save_server(str(uuid.uuid4()), body, request)

        @app.put("/api/admin/servers/{server_id}", dependencies=admin, tags=["catalog-admin"])
        async def update_server(server_id: uuid.UUID, body: ServerEdit, request: Request):
            self.require_server(server_id)
            return save_server(str(server_id), body, request)

        @app.get("/api/admin/servers/{server_id}/icon", dependencies=admin, tags=["catalog-admin"])
        def admin_icon(server_id: uuid.UUID):
            return self.icon(self.require_server(server_id))

        @app.get("/api/v1/servers", tags=["launcher-servers"])
        def public_servers(include_offline: bool = False, device=Depends(device_auth)):
            items = []
            for k, v in self.configs().items():
                if self.visible(v, device):
                    item = self.render(k, v)
                    if include_offline or item["state"] not in ("stopped", "stopping"):
                        items.append(item)
            return {"items": sorted(items, key=lambda v: (v["order"], v["name"])), "checked_at": self.sync_at,
                    "stale": bool(self.sync_error or time.time() - self.sync_at > 90), "refresh_after": 30,
                    "launch_target": "homepage_selected_instance"}

        @app.get("/api/v1/servers/{server_id}", tags=["launcher-servers"])
        def server_detail(server_id: uuid.UUID, device=Depends(device_auth)):
            return self.render(str(server_id), self.require_server(server_id, device))

        @app.get("/api/v1/servers/{server_id}/icon", tags=["launcher-servers"])
        def public_icon(server_id: uuid.UUID, device=Depends(device_auth)):
            return self.icon(self.require_server(server_id, device))

        @app.post("/api/v1/servers/{server_id}/pack", tags=["launcher-servers"])
        def related_pack(server_id: uuid.UUID, request: Request, device=Depends(device_auth)):
            cfg = self.require_server(server_id, device)
            if not cfg.get("version_id"):
                self.fail(404, "no_related_instance", "暂无相关实例")
            value = self.version(cfg["version_id"])
            if value["archived_at"]:
                self.fail(404, "no_related_instance", "暂无相关实例")
            import secrets
            lease = secrets.token_urlsafe(32)
            expiry = int(time.time()) + 86400
            with self.db() as c:
                c.execute("INSERT INTO download_leases VALUES(?,?,?,?)", (self.digest("download", lease), device["id"], value["id"], expiry))
                self.audit(c, request, "pack_download_requested", value["id"], device["qq_id"])
            return {**value, "download_url": f'/api/v1/packs/{value["id"]}/download', "download_lease": lease,
                    "lease_expires_at": expiry, "download_headers": ["Authorization: Bearer <device_token>", "X-Download-Lease: <download_lease>"],
                    "install_action": "transfer_then_homepage", "auto_launch": False}

        @app.api_route("/api/v1/packs/{version_id}/download", methods=["GET", "HEAD"], tags=["launcher-servers"])
        async def public_download(version_id: uuid.UUID, request: Request, device=Depends(device_auth)):
            lease = request.headers.get("x-download-lease", "")
            if len(lease) > 128:
                self.fail(403, "lease_invalid", "下载凭证无效，请重新获取相关实例")
            with self.db() as c:
                row = c.execute("SELECT 1 FROM download_leases WHERE token_hash=? AND device_id=? AND version_id=? AND expires_at>?",
                                (self.digest("download", lease), device["id"], str(version_id), int(time.time()))).fetchone()
            if not row:
                self.fail(403, "lease_invalid", "下载凭证已过期，请重新获取相关实例")
            return await self.download(request, self.version(version_id), device["qq_id"])

        @app.get("/api/admin/packs", dependencies=admin, tags=["catalog-admin"])
        def packs():
            with self.db() as c:
                rows = [dict(r) for r in c.execute("SELECT * FROM packs ORDER BY created_at DESC")]
                versions = [dict(r) for r in c.execute("SELECT * FROM pack_versions ORDER BY created_at DESC")]
                uploads = [self.upload_row(r[0]) for r in c.execute("SELECT id FROM pack_uploads WHERE status IN ('uploading','failed') ORDER BY created_at DESC LIMIT 20")]
            configs = self.configs()
            for v in versions:
                v["servers"] = [s["name"] for s in configs.values() if s.get("version_id") == v["id"]]
            for row in rows:
                row["versions"] = [v for v in versions if v["pack_id"] == row["id"]]
            return {"items": rows, "uploads": uploads, "storage": self.usage()}

        @app.put("/api/admin/packs/{pack_id}", dependencies=admin, tags=["catalog-admin"])
        def edit_pack(pack_id: uuid.UUID, body: PackEdit, request: Request):
            with self.db() as c:
                if not c.execute("UPDATE packs SET name=? WHERE id=?", (body.name, str(pack_id))).rowcount:
                    self.fail(404, "pack_missing", "整合包不存在")
                self.audit(c, request, "pack_renamed", str(pack_id), body.name)
            return {"saved": True}

        @app.post("/api/admin/packs/versions/{version_id}/archive", dependencies=admin, tags=["catalog-admin"])
        async def archive_version(version_id: uuid.UUID, request: Request):
            v = self.version(version_id)
            if any(s.get("version_id") == str(version_id) for s in self.configs().values()):
                self.fail(409, "pack_linked", "请先解除服务器关联，再归档此版本")
            with self.db() as c:
                c.execute("UPDATE pack_versions SET archived_at=? WHERE id=?", (int(time.time()), str(version_id)))
                self.audit(c, request, "pack_archived", str(version_id), v["name"] + " " + v["version"])
            return {"archived": True}

        @app.post("/api/admin/packs/versions/{version_id}/restore", dependencies=admin, tags=["catalog-admin"])
        async def restore_version(version_id: uuid.UUID, request: Request):
            v = self.version(version_id)
            with self.db() as c:
                c.execute("UPDATE pack_versions SET archived_at=NULL WHERE id=?", (str(version_id),))
                self.audit(c, request, "pack_restored", str(version_id), v["name"] + " " + v["version"])
            return {"restored": True}

        @app.api_route("/api/admin/packs/versions/{version_id}/download", methods=["GET", "HEAD"], dependencies=admin, tags=["catalog-admin"])
        async def admin_download(version_id: uuid.UUID, request: Request):
            return await self.download(request, self.version(version_id), "管理员", True)

        @app.post("/api/admin/uploads", dependencies=admin, tags=["catalog-admin"])
        async def create_upload(body: Upload, request: Request):
            usage = self.usage()
            if body.size > self.traffic["max_file_gib"] * GIB:
                self.fail(413, "file_too_large", "文件超出当前单文件大小限制")
            if usage["stored_bytes"] + usage["reserved_bytes"] + body.size > usage["quota_bytes"] or usage["free_disk_bytes"] - usage["reserved_bytes"] - body.size < 2 * GIB:
                self.fail(507, "storage_full", "存储配额或磁盘剩余空间不足")
            now, upload_id = int(time.time()), str(uuid.uuid4())
            with self.db() as c:
                c.execute("BEGIN IMMEDIATE")
                if c.execute("SELECT COUNT(*) FROM pack_uploads WHERE status='uploading'").fetchone()[0] >= 8:
                    self.fail(409, "uploads_pending", "请完成或取消现有上传任务后再上传")
                pack_id = str(body.pack_id) if body.pack_id else str(uuid.uuid4())
                if body.pack_id:
                    if not c.execute("SELECT 1 FROM packs WHERE id=?", (pack_id,)).fetchone():
                        self.fail(404, "pack_missing", "整合包不存在")
                else:
                    c.execute("INSERT INTO packs VALUES(?,?,?)", (pack_id, body.name, now))
                if c.execute("SELECT 1 FROM pack_versions WHERE pack_id=? AND version=?", (pack_id, body.version)).fetchone() or c.execute(
                        "SELECT 1 FROM pack_uploads WHERE pack_id=? AND version=? AND status='uploading'", (pack_id, body.version)).fetchone():
                    self.fail(409, "version_exists", "该版本已存在，请使用新的版本号")
                c.execute("INSERT INTO pack_uploads(id,pack_id,version,filename,size,created_at,updated_at,fingerprint) VALUES(?,?,?,?,?,?,?,?)",
                          (upload_id, pack_id, body.version, body.filename, body.size, now, now, body.fingerprint))
                self.audit(c, request, "pack_upload_started", upload_id, body.filename)
            return self.upload_row(upload_id)

        @app.get("/api/admin/uploads/{upload_id}", dependencies=admin, tags=["catalog-admin"])
        def upload_status(upload_id: uuid.UUID):
            return self.upload_row(upload_id)

        @app.put("/api/admin/uploads/{upload_id}/chunk", dependencies=admin, tags=["catalog-admin"])
        async def put_chunk(upload_id: uuid.UUID, request: Request, offset: int = Query(ge=0)):
            uid = str(upload_id)
            row = self.upload_row(uid)
            length = request.headers.get("content-length", "")
            if not length.isdecimal() or not 0 < int(length) <= CHUNK:
                self.fail(413, "invalid_chunk", "分片必须为 1 MiB 以内且提供 Content-Length")
            if row["status"] != "uploading":
                self.fail(409, "upload_closed", "此上传已结束，请重新上传")
            if uid in self.upload_locks or uid in self.finalizing or self.upload_active >= self.traffic["upload_slots"]:
                from fastapi import HTTPException
                raise HTTPException(429, detail={"code": "upload_busy", "message": "上传通道繁忙，稍后可继续"}, headers={"Retry-After": "3"})
            if offset != row["offset"] or offset + int(length) > row["size"]:
                self.fail(409, "offset_conflict", "上传进度不一致，请重新选择原文件续传")
            path = self.storage / "staging" / (uid + ".part")
            self.upload_locks.add(uid)
            self.upload_active += 1
            self.active[uid] = {"id": uid, "direction": "upload", "name": row["filename"], "owner": "管理员",
                                "bytes": offset, "size": row["size"], "started_at": row["created_at"]}
            pacer, received = Pacer(), 0
            try:
                with path.open("ab") as f:
                    async for part in request.stream():
                        received += len(part)
                        if received > int(length):
                            self.fail(413, "invalid_chunk", "分片长度超出声明")
                        for start in range(0, len(part), BLOCK):
                            block = part[start:start + BLOCK]
                            await asyncio.gather(self.up_pacer.wait(len(block), self.traffic["upload_total_mib"] * MIB),
                                                 pacer.wait(len(block), self.traffic["upload_task_mib"] * MIB))
                            f.write(block)
                            self.bytes_up += len(block)
                            self.active[uid]["bytes"] += len(block)
                    if received != int(length):
                        self.fail(400, "incomplete_chunk", "分片接收不完整，可重新选择原文件续传")
                    f.flush()
                    os.fsync(f.fileno())
                with self.db() as c:
                    c.execute("UPDATE pack_uploads SET updated_at=? WHERE id=?", (int(time.time()), uid))
            finally:
                self.upload_locks.discard(uid)
                self.upload_active -= 1
                self.active.pop(uid, None)
            return self.upload_row(uid)

        @app.post("/api/admin/uploads/{upload_id}/complete", dependencies=admin, tags=["catalog-admin"])
        async def finish_upload(upload_id: uuid.UUID, request: Request):
            uid = str(upload_id)
            row = self.upload_row(uid)
            if row["status"] == "complete":
                return self.version(row["version_id"])
            if row["status"] != "uploading" or uid in self.upload_locks or self.finalizing:
                self.fail(409, "upload_busy", "上传或校验正在进行，请稍后重试")
            if row["offset"] != row["size"]:
                self.fail(409, "upload_incomplete", "文件尚未上传完整")
            self.finalizing.add(uid)
            path = self.storage / "staging" / (uid + ".part")
            try:
                try:
                    def check_fingerprint():
                        with path.open("rb") as f:
                            first = f.read(65536)
                            f.seek(max(0, row["size"] - 65536))
                            last = f.read(65536)
                        return hashlib.sha256(first + last + str(row["size"]).encode()).hexdigest()
                    if await asyncio.to_thread(check_fingerprint) != row["fingerprint"]:
                        raise ValueError("续传文件与原文件不一致，请取消后重新上传")
                    metadata = await asyncio.to_thread(inspect_pack, path)
                except ValueError as e:
                    with self.db() as c:
                        c.execute("UPDATE pack_uploads SET status='failed',error=? WHERE id=?", (str(e), uid))
                    self.fail(422, "pack_invalid", str(e))
                vid = uid
                blob = self.storage / "blobs" / (vid + ".pack")
                path.replace(blob)
                try:
                    with self.db() as c:
                        c.execute("INSERT INTO pack_versions VALUES(?,?,?,?,?,?,?,?,?,?,NULL)",
                                  (vid, row["pack_id"], row["version"], row["filename"], row["size"], metadata["sha256"], metadata["format"],
                                   metadata["minecraft"], metadata["loader"], int(time.time())))
                        c.execute("UPDATE pack_uploads SET status='complete',version_id=? WHERE id=?", (vid, uid))
                        self.audit(c, request, "pack_uploaded", vid, row["filename"])
                except Exception:
                    blob.replace(path)
                    raise
                return self.version(vid)
            finally:
                self.finalizing.discard(uid)

        @app.delete("/api/admin/uploads/{upload_id}", dependencies=admin, tags=["catalog-admin"])
        async def cancel_upload(upload_id: uuid.UUID, request: Request):
            uid = str(upload_id)
            row = self.upload_row(uid)
            if uid in self.upload_locks or uid in self.finalizing:
                self.fail(409, "upload_busy", "正在处理分片，请暂停后再取消")
            if row["status"] == "complete":
                self.fail(409, "already_complete", "文件已入库，请通过归档管理")
            (self.storage / "staging" / (uid + ".part")).unlink(missing_ok=True)
            with self.db() as c:
                c.execute("UPDATE pack_uploads SET status='cancelled' WHERE id=?", (uid,))
                self.audit(c, request, "pack_upload_cancelled", uid, row["filename"])
            return {"cancelled": True}
