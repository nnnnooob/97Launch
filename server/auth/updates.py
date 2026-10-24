"""Signed, immutable launcher releases sharing the catalog's transfer budget."""
from __future__ import annotations

import asyncio
import base64
import hashlib
import json
import os
import re
import struct
import time
import uuid
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from fastapi import Depends, Query, Request
from pydantic import BaseModel, ConfigDict, Field

from catalog import BLOCK, CHUNK, GIB, MIB, Pacer


class SignedManifest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    payload: str = Field(max_length=12000)
    signature: str = Field(max_length=1200)
    key_id: str = Field(pattern=r"^[a-f0-9]{16}$")


class ReleaseUpload(BaseModel):
    model_config = ConfigDict(extra="forbid")
    manifest: SignedManifest
    notes: str = Field(default="", max_length=5000)


class Releases:
    def __init__(self, catalog, rate, device_auth):
        self.catalog, self.rate, self.device_auth = catalog, rate, device_auth
        self.db, self.fail = catalog.db, catalog.fail
        self.storage = catalog.storage.parent / "updates"
        self.key = serialization.load_pem_public_key((catalog.root / "update-signing-public.pem").read_bytes())
        if not isinstance(self.key, rsa.RSAPublicKey) or self.key.key_size < 3072:
            raise ValueError("Invalid release signing public key")
        der = self.key.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
        self.key_id = hashlib.sha256(der).hexdigest()[:16]

    def start(self):
        self.storage.mkdir(mode=0o700, parents=True, exist_ok=True)
        with self.db() as c:
            c.executescript("""
                CREATE TABLE IF NOT EXISTS launcher_releases(
                    id TEXT PRIMARY KEY,version TEXT NOT NULL,build INTEGER NOT NULL,
                    channel TEXT NOT NULL,architecture TEXT NOT NULL,size INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,manifest TEXT NOT NULL,notes TEXT NOT NULL,
                    state TEXT NOT NULL DEFAULT 'uploading',error TEXT NOT NULL DEFAULT '',
                    created_at INTEGER NOT NULL,updated_at INTEGER NOT NULL,published_at INTEGER,
                    downloads INTEGER NOT NULL DEFAULT 0,UNIQUE(build,architecture));
            """)
        self.catalog.extra_cleanup = self.cleanup

    def cleanup(self):
        with self.db() as c:
            for row in c.execute("SELECT id FROM launcher_releases WHERE state IN ('uploading','failed') AND updated_at<?", (int(time.time()) - 86400,)).fetchall():
                if row["id"] in self.catalog.upload_locks or row["id"] in self.catalog.finalizing:
                    continue
                self.path(row["id"]).unlink(missing_ok=True)
                c.execute("UPDATE launcher_releases SET state='expired',error='上传已过期' WHERE id=?", (row["id"],))

    def path(self, release_id):
        return self.storage / (str(uuid.UUID(str(release_id))) + ".exe")

    def row(self, release_id):
        with self.db() as c:
            row = c.execute("SELECT * FROM launcher_releases WHERE id=?", (str(release_id),)).fetchone()
        if row is None:
            self.fail(404, "release_missing", "应用版本不存在")
        result = dict(row)
        result["manifest"] = json.loads(result["manifest"])
        result["offset"] = self.path(result["id"]).stat().st_size if self.path(result["id"]).exists() else 0
        return result

    def verify_manifest(self, manifest):
        try:
            raw = base64.b64decode(manifest.payload, validate=True)
            sig = base64.b64decode(manifest.signature, validate=True)
            if len(raw) > 8192 or manifest.key_id != self.key_id:
                raise ValueError()
            self.key.verify(sig, raw, padding.PKCS1v15(), hashes.SHA256())
            data = json.loads(raw)
            expected = {"format", "app", "version", "build", "channel", "architecture", "size", "sha256", "issued_at"}
            if set(data) != expected or data["format"] != 1 or data["app"] != "97Launch":
                raise ValueError()
            if not isinstance(data["version"], str) or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", data["version"]):
                raise ValueError()
            if type(data["build"]) is not int or not 1 <= data["build"] <= 65535:
                raise ValueError()
            if type(data["size"]) is not int or not 65536 <= data["size"] <= 512 * MIB:
                raise ValueError()
            if data["channel"] not in ("preview", "stable") or data["architecture"] not in ("x64", "arm64"):
                raise ValueError()
            if data["channel"] == "stable" and "-" in data["version"]:
                raise ValueError()
            if not re.fullmatch(r"[0-9a-f]{64}", data["sha256"]):
                raise ValueError()
            if type(data["issued_at"]) is not int or data["issued_at"] > time.time() + 86400:
                raise ValueError()
            return data
        except Exception:
            self.fail(422, "manifest_invalid", "发布清单或数字签名无效，请使用发布工具生成")

    def public(self, row):
        return {**{k: row[k] for k in ("id", "version", "build", "channel", "architecture", "size", "notes", "published_at", "manifest")},
                "download_url": f'/api/v1/updates/{row["id"]}/download'}

    def available(self, release_id):
        with self.db() as c:
            row = c.execute("SELECT state FROM launcher_releases WHERE id=?", (str(release_id),)).fetchone()
        return row is not None and row["state"] == "published"

    def install_routes(self, app, admin_auth):
        admin = [Depends(admin_auth)]

        @app.get("/api/v1/updates/check", tags=["updates"])
        def check(request: Request, channel: str = Query(default="preview", pattern="^(preview|stable)$"),
                  architecture: str = Query(default="x64", pattern="^(x64|arm64)$"), build: int = Query(default=0, ge=0, le=65535)):
            self.rate(request, "update-check", 30, 60)
            with self.db() as c:
                row = c.execute("SELECT id FROM launcher_releases WHERE state='published' AND architecture=? AND (channel=? OR channel='stable') ORDER BY build DESC LIMIT 1", (architecture, channel)).fetchone()
            latest = self.row(row["id"]) if row else None
            return {"available": latest is not None and latest["build"] > build,
                    "release": self.public(latest) if latest else None, "server_time": int(time.time())}

        @app.get("/api/v1/updates/{release_id}", tags=["updates"])
        def published(release_id: uuid.UUID, request: Request):
            self.rate(request, "update-check", 30, 60)
            row = self.row(release_id)
            if row["state"] != "published":
                self.fail(410, "release_withdrawn", "该版本已撤回，请重新检查更新")
            return self.public(row)

        @app.api_route("/api/v1/updates/{release_id}/download", methods=["GET", "HEAD"], tags=["updates"])
        async def download(release_id: uuid.UUID, request: Request):
            self.rate(request, "update-download", 30, 60)
            row = self.row(release_id)
            if row["state"] != "published":
                self.fail(410, "release_withdrawn", "该版本已撤回，请重新检查更新")
            owner = "更新:" + self.catalog.digest("update-ip", request.client.host)[:12]
            authorization = request.headers.get("authorization", "")
            if authorization.startswith("Bearer "):
                from fastapi.security import HTTPAuthorizationCredentials
                from fastapi import HTTPException
                try:
                    device = self.device_auth(request, HTTPAuthorizationCredentials(scheme="Bearer", credentials=authorization[7:]))
                    owner = device["qq_id"]
                except HTTPException:
                    pass  # Updates remain available when a device credential needs repair.
            if request.method == "GET":
                with self.db() as c:
                    c.execute("UPDATE launcher_releases SET downloads=downloads+1 WHERE id=?", (str(release_id),))
            return await self.catalog.download(request, {**row, "filename": "97Launch.exe"}, owner,
                                               path=self.path(release_id), available=lambda: self.available(release_id))

        @app.get("/api/admin/releases", dependencies=admin, tags=["updates-admin"])
        def listing():
            with self.db() as c:
                ids = [r[0] for r in c.execute("SELECT id FROM launcher_releases ORDER BY build DESC,created_at DESC LIMIT 200")]
                maximum = c.execute("SELECT COALESCE(MAX(build),0) FROM launcher_releases").fetchone()[0]
            return {"items": [self.row(i) for i in ids], "next_build": maximum + 1, "signing_key_id": self.key_id}

        @app.post("/api/admin/releases", dependencies=admin, tags=["updates-admin"])
        def create(body: ReleaseUpload, request: Request):
            data = self.verify_manifest(body.manifest)
            usage = self.catalog.usage()
            if usage["stored_bytes"] + usage["reserved_bytes"] + data["size"] > usage["quota_bytes"] or usage["free_disk_bytes"] - usage["reserved_bytes"] - data["size"] < 2 * GIB:
                self.fail(507, "storage_full", "存储配额或磁盘剩余空间不足")
            now, uid = int(time.time()), str(uuid.uuid4())
            with self.db() as c:
                c.execute("BEGIN IMMEDIATE")
                if c.execute("SELECT 1 FROM launcher_releases WHERE build=? AND architecture=?", (data["build"], data["architecture"])).fetchone():
                    self.fail(409, "build_exists", "构建编号已存在，请续传原任务或使用新编号")
                if c.execute("SELECT COUNT(*) FROM launcher_releases WHERE state='uploading'").fetchone()[0] >= 8:
                    self.fail(409, "uploads_pending", "请先完成或取消现有应用上传")
                c.execute("INSERT INTO launcher_releases(id,version,build,channel,architecture,size,sha256,manifest,notes,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                          (uid, data["version"], data["build"], data["channel"], data["architecture"], data["size"], data["sha256"], body.manifest.model_dump_json(), body.notes, now, now))
                self.catalog.audit(c, request, "release_upload_started", uid, data["version"])
            return self.row(uid)

        @app.get("/api/admin/releases/{release_id}", dependencies=admin, tags=["updates-admin"])
        def status(release_id: uuid.UUID):
            return self.row(release_id)

        @app.put("/api/admin/releases/{release_id}/chunk", dependencies=admin, tags=["updates-admin"])
        async def chunk(release_id: uuid.UUID, request: Request, offset: int = Query(ge=0)):
            row, cat = self.row(release_id), self.catalog
            uid = row["id"]
            length = request.headers.get("content-length", "")
            if not length.isdecimal() or not 0 < int(length) <= CHUNK:
                self.fail(413, "invalid_chunk", "每个分片需要提供长度且不能超过 1 MiB")
            if row["state"] != "uploading":
                self.fail(409, "upload_closed", "该上传已结束")
            if uid in cat.upload_locks or uid in cat.finalizing or cat.upload_active >= cat.traffic["upload_slots"]:
                self.fail(429, "upload_busy", "上传通道繁忙，请稍后继续")
            if offset != row["offset"] or offset + int(length) > row["size"]:
                self.fail(409, "offset_conflict", "上传进度不一致，请读取最新进度后续传")
            cat.upload_locks.add(uid)
            cat.upload_active += 1
            cat.active[uid] = {"id": uid, "direction": "upload", "name": "97Launch " + row["version"], "owner": "管理员", "bytes": offset, "size": row["size"], "started_at": row["created_at"]}
            pacer, received = Pacer(), 0
            try:
                with self.path(uid).open("ab") as f:
                    try:
                        async for part in request.stream():
                            received += len(part)
                            if received > int(length):
                                self.fail(413, "invalid_chunk", "分片超出声明长度")
                            for i in range(0, len(part), BLOCK):
                                block = part[i:i + BLOCK]
                                await asyncio.gather(cat.up_pacer.wait(len(block), cat.traffic["upload_total_mib"] * MIB), pacer.wait(len(block), cat.traffic["upload_task_mib"] * MIB))
                                f.write(block)
                                cat.bytes_up += len(block)
                                cat.active[uid]["bytes"] += len(block)
                        if received != int(length):
                            self.fail(400, "incomplete_chunk", "分片接收不完整，可继续上传")
                        f.flush()
                        os.fsync(f.fileno())
                    except BaseException:
                        f.truncate(offset)
                        raise
                with self.db() as c:
                    c.execute("UPDATE launcher_releases SET updated_at=? WHERE id=?", (int(time.time()), uid))
            finally:
                cat.upload_locks.discard(uid)
                cat.upload_active -= 1
                cat.active.pop(uid, None)
            return self.row(uid)

        @app.post("/api/admin/releases/{release_id}/complete", dependencies=admin, tags=["updates-admin"])
        async def complete(release_id: uuid.UUID, request: Request):
            row = self.row(release_id)
            uid = row["id"]
            if row["state"] != "uploading" or row["offset"] != row["size"] or uid in self.catalog.upload_locks or uid in self.catalog.finalizing:
                self.fail(409, "upload_incomplete", "文件尚未完整上传或正在校验")
            self.catalog.finalizing.add(uid)
            def inspect():
                with self.path(uid).open("rb") as f:
                    digest = hashlib.sha256()
                    for block in iter(lambda: f.read(MIB), b""):
                        digest.update(block)
                    sha = digest.hexdigest()
                    f.seek(0)
                    if f.read(2) != b"MZ":
                        raise ValueError()
                    f.seek(0x3c)
                    offset = struct.unpack("<I", f.read(4))[0]
                    if offset > row["size"] - 6:
                        raise ValueError()
                    f.seek(offset)
                    if f.read(4) != b"PE\0\0":
                        raise ValueError()
                    machine = struct.unpack("<H", f.read(2))[0]
                if sha != row["sha256"] or machine != {"x64": 0x8664, "arm64": 0xaa64}[row["architecture"]]:
                    raise ValueError()
            try:
                await asyncio.to_thread(inspect)
            except Exception:
                with self.db() as c:
                    c.execute("UPDATE launcher_releases SET state='failed',error='文件哈希或目标架构校验失败' WHERE id=?", (uid,))
                self.fail(422, "release_invalid", "文件哈希或架构与签名清单不符")
            finally:
                self.catalog.finalizing.discard(uid)
            with self.db() as c:
                c.execute("UPDATE launcher_releases SET state='ready',updated_at=? WHERE id=?", (int(time.time()), uid))
                self.catalog.audit(c, request, "release_verified", uid, row["version"])
            return self.row(uid)

        @app.post("/api/admin/releases/{release_id}/publish", dependencies=admin, tags=["updates-admin"])
        def publish(release_id: uuid.UUID, request: Request):
            row = self.row(release_id)
            if row["state"] not in ("ready", "withdrawn") or row["offset"] != row["size"]:
                self.fail(409, "release_not_ready", "请完成上传和校验后再发布")
            self.verify_manifest(SignedManifest(**row["manifest"]))
            with self.db() as c:
                c.execute("BEGIN IMMEDIATE")
                maximum = c.execute("SELECT COALESCE(MAX(build),0) FROM launcher_releases WHERE state='published' AND architecture=? AND channel=?", (row["architecture"], row["channel"])).fetchone()[0]
                if row["build"] < maximum:
                    self.fail(409, "older_build", "请发布更高编号的构建")
                c.execute("UPDATE launcher_releases SET state='published',published_at=?,updated_at=? WHERE id=?", (int(time.time()), int(time.time()), row["id"]))
                self.catalog.audit(c, request, "release_published", row["id"], row["version"])
            return self.row(release_id)

        @app.post("/api/admin/releases/{release_id}/withdraw", dependencies=admin, tags=["updates-admin"])
        def withdraw(release_id: uuid.UUID, request: Request):
            row = self.row(release_id)
            if row["state"] != "published":
                self.fail(409, "release_not_published", "该版本当前未发布")
            with self.db() as c:
                c.execute("UPDATE launcher_releases SET state='withdrawn',updated_at=? WHERE id=?", (int(time.time()), row["id"]))
                self.catalog.audit(c, request, "release_withdrawn", row["id"], row["version"])
            return self.row(release_id)

        @app.delete("/api/admin/releases/{release_id}", dependencies=admin, tags=["updates-admin"])
        def cancel(release_id: uuid.UUID, request: Request):
            row = self.row(release_id)
            if row["state"] not in ("uploading", "failed") or row["id"] in self.catalog.upload_locks or row["id"] in self.catalog.finalizing:
                self.fail(409, "release_in_use", "仅可取消未完成且空闲的上传")
            self.path(release_id).unlink(missing_ok=True)
            with self.db() as c:
                c.execute("UPDATE launcher_releases SET state='cancelled' WHERE id=?", (row["id"],))
                self.catalog.audit(c, request, "release_upload_cancelled", row["id"], row["version"])
            return {"cancelled": True}
