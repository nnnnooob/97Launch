"""97Launch pairing service. Run one uvicorn worker; SQLite owns durable state."""
from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import secrets
import sqlite3
import time
import uuid
from contextlib import asynccontextmanager, contextmanager
from pathlib import Path
from typing import Annotated

from fastapi import Depends, FastAPI, HTTPException, Query, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, ConfigDict, Field

ROOT = Path(__file__).parent
DB_PATH = Path(os.environ.get("AUTH_DATABASE", "/var/lib/97launch-auth/auth.sqlite3"))
CONFIG = json.loads(Path(os.environ.get("AUTH_SECRETS", "/etc/97launch-auth/secrets.json")).read_text())
ORIGIN = os.environ.get("AUTH_ORIGIN", "http://127.0.0.1:18797").rstrip("/")
LOCAL_ORIGIN = os.environ.get("AUTH_LOCAL_ADMIN_ORIGIN", "").rstrip("/")
PUBLIC_READY = os.environ.get("AUTH_PUBLIC_READY", "false").lower() == "true"
COOKIE = "__Host-launch_admin"
SESSION_TTL = 8 * 3600
CODE_TTL = 300
bearer = HTTPBearer(auto_error=False)


@contextmanager
def db():
    connection = sqlite3.connect(DB_PATH, timeout=15)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA foreign_keys=ON")
    try:
        with connection:
            yield connection
    finally:
        connection.close()


def digest(kind: str, value: str) -> str:
    return hmac.new(CONFIG["hash_key"].encode(), f"{kind}:{value}".encode(), hashlib.sha256).hexdigest()


def fail(status: int, code: str, message: str):
    raise HTTPException(status, detail={"code": code, "message": message})


def ip(request: Request) -> str:
    return request.client.host if request.client else "unknown"


def rate(request: Request, scope: str, limit: int, seconds: int, identity: str = ""):
    now = int(time.time())
    window = now // seconds
    key = digest("rate", f"{scope}:{identity or ip(request)}")
    with db() as c:
        c.execute("INSERT INTO rates VALUES(?,?,1,?) ON CONFLICT(key,window) DO UPDATE SET hits=hits+1",
                  (key, window, now + seconds * 2))
        hits = c.execute("SELECT hits FROM rates WHERE key=? AND window=?", (key, window)).fetchone()[0]
        c.execute("DELETE FROM rates WHERE expires_at < ?", (now,))
    if hits > limit:
        raise HTTPException(429, detail={"code": "rate_limited", "message": "操作过于频繁，请稍后重试"},
                            headers={"Retry-After": str(seconds - now % seconds)})


def audit(c, request: Request, event: str, subject: str = "", detail: str = ""):
    c.execute("INSERT INTO audit(created_at,event,subject,detail,ip) VALUES(?,?,?,?,?)",
              (int(time.time()), event, subject, detail, ip(request)))


def policy(c):
    return json.loads(c.execute("SELECT value FROM settings WHERE key='policy'").fetchone()[0])


@asynccontextmanager
async def lifespan(_):
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    with db() as c:
        c.execute("PRAGMA journal_mode=WAL")
        c.executescript("""
            CREATE TABLE IF NOT EXISTS codes(
                id TEXT PRIMARY KEY, code_hash TEXT UNIQUE NOT NULL, code_hint TEXT NOT NULL,
                qq_id TEXT NOT NULL, group_id TEXT NOT NULL, nickname TEXT NOT NULL,
                created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL,
                used_at INTEGER, revoked_at INTEGER, device_id TEXT);
            CREATE TABLE IF NOT EXISTS devices(
                id TEXT PRIMARY KEY, installation_id TEXT UNIQUE NOT NULL, token_hash TEXT UNIQUE NOT NULL,
                qq_id TEXT NOT NULL, nickname TEXT NOT NULL, group_id TEXT NOT NULL,
                name TEXT NOT NULL, platform TEXT NOT NULL, app_version TEXT NOT NULL,
                created_at INTEGER NOT NULL, last_seen_at INTEGER NOT NULL, last_ip TEXT NOT NULL,
                revoked_at INTEGER);
            CREATE TABLE IF NOT EXISTS sessions(token_hash TEXT PRIMARY KEY, expires_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS rates(key TEXT, window INTEGER, hits INTEGER, expires_at INTEGER,
                PRIMARY KEY(key,window));
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT, created_at INTEGER,
                event TEXT, subject TEXT, detail TEXT, ip TEXT);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS codes_user ON codes(qq_id,created_at);
            CREATE INDEX IF NOT EXISTS devices_user ON devices(qq_id);
            CREATE INDEX IF NOT EXISTS audit_created ON audit(created_at);
        """)
        c.execute("INSERT OR IGNORE INTO settings VALUES('policy',?)", (json.dumps({
            "allowed_group_ids": [], "allowed_qq_ids": [], "max_devices_per_user": 3
        }),))
        now = int(time.time())
        c.execute("UPDATE codes SET revoked_at=? WHERE used_at IS NULL AND revoked_at IS NULL "
                  "AND expires_at>? AND substr(code_hint,1,4)<>'97p_'", (now, now))
    await catalog.start()
    updates.start()
    try:
        yield
    finally:
        await catalog.stop()


app = FastAPI(title="97Launch 服务 API", version="1.5.0", lifespan=lifespan,
              docs_url=None, redoc_url=None, openapi_url=None)


@app.middleware("http")
async def headers(request: Request, call_next):
    length = request.headers.get("content-length", "0")
    if not length.isdecimal():
        return JSONResponse({"error": {"code": "invalid_request", "message": "请求长度无效"}}, status_code=400)
    chunk_upload = request.method == "PUT" and re.fullmatch(r"/api/admin/(?:uploads|releases)/[0-9a-f-]{36}/chunk", request.url.path)
    if int(length) > (1048576 if chunk_upload else 16384):
        return JSONResponse({"error": {"code": "body_too_large", "message": "请求过大"}}, status_code=413)
    response = await call_next(request)
    response.headers.update({
        "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
        "Referrer-Policy": "no-referrer", "X-Frame-Options": "DENY",
        "Content-Security-Policy": "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; "
                                   "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
    })
    return response


@app.exception_handler(HTTPException)
async def http_error(_, error):
    detail = error.detail if isinstance(error.detail, dict) else {"code": "request_failed", "message": str(error.detail)}
    return JSONResponse({"error": detail}, status_code=error.status_code, headers=error.headers)


@app.exception_handler(RequestValidationError)
async def validation_error(_, error):
    # Pydantic's default response echoes input, which may contain a credential.
    return JSONResponse({"error": {"code": "invalid_request", "message": "参数格式不正确",
                                   "fields": [".".join(map(str, e["loc"])) for e in error.errors()]}}, status_code=422)


class Body(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


QQ = Annotated[str, Field(pattern=r"^\d{5,20}$")]


class Issue(Body):
    qq_id: QQ
    group_id: QQ
    nickname: str = Field(default="", max_length=64)


class BotRevoke(Body):
    qq_id: QQ
    group_id: QQ


class Redeem(Body):
    code: str = Field(min_length=1, max_length=128)
    installation_id: uuid.UUID
    device_name: str = Field(min_length=1, max_length=80)
    platform: str = Field(default="windows", min_length=1, max_length=32)
    app_version: str = Field(min_length=1, max_length=32)


class Heartbeat(Body):
    app_version: str = Field(min_length=1, max_length=32)


class Login(Body):
    key: str = Field(min_length=20, max_length=200)


class Policy(Body):
    allowed_group_ids: list[QQ] = Field(max_length=100)
    allowed_qq_ids: list[QQ] = Field(max_length=500)
    max_devices_per_user: int = Field(ge=1, le=20)


def bot_auth(request: Request, auth: HTTPAuthorizationCredentials | None = Depends(bearer)):
    rate(request, "bot-auth", 120, 60)
    value = auth.credentials if auth else ""
    if not hmac.compare_digest(digest("bot", value), CONFIG["bot_key_hash"]):
        fail(401, "bot_unauthorized", "机器人接口密钥无效")


def local_admin(request: Request) -> bool:
    # Only the loopback-bound SSH forward uses this origin; Nginx overwrites Host/X-Forwarded-For.
    return bool(LOCAL_ORIGIN and ip(request) == "127.0.0.1" and
                str(request.base_url).rstrip("/") == LOCAL_ORIGIN)


def admin_cookie(request: Request) -> str:
    return "launch_admin_local" if local_admin(request) else COOKIE


def admin_auth(request: Request):
    value = request.cookies.get(admin_cookie(request), "")
    with db() as c:
        session = c.execute("SELECT expires_at FROM sessions WHERE token_hash=?", (digest("admin-session", value),)).fetchone()
    if not session or session[0] <= time.time():
        fail(401, "admin_unauthorized", "请重新输入管理密钥")
    origin = LOCAL_ORIGIN if local_admin(request) else ORIGIN
    if request.method not in ("GET", "HEAD") and request.headers.get("origin") != origin:
        fail(403, "origin_denied", "请求来源不正确，请从管理网页操作")


def device_auth(request: Request, auth: HTTPAuthorizationCredentials | None = Depends(bearer)):
    rate(request, "device-api", 90, 60)
    with db() as c:
        row = c.execute("SELECT * FROM devices WHERE token_hash=?", (digest("device", auth.credentials if auth else ""),)).fetchone()
    if row is None:
        fail(401, "device_unauthorized", "设备凭据无效，请重新配对")
    if row["revoked_at"]:
        fail(403, "device_revoked", "此设备授权已撤销，请联系管理员")
    return dict(row)


def device_public(row):
    return {k: row[k] for k in ("id", "qq_id", "nickname", "group_id", "name", "platform", "app_version",
                                "created_at", "last_seen_at", "last_ip", "revoked_at")}


def issue_code(body: Issue, request: Request, *, admin: bool = False):
    now = int(time.time())
    rate(request, "issue-user", 3, 60, body.qq_id)
    rate(request, "issue-group", 60, 60, body.group_id)
    with db() as c:
        c.execute("BEGIN IMMEDIATE")
        p = policy(c)
        if body.group_id not in p["allowed_group_ids"]:
            fail(403, "group_denied", "此群尚未开放配对，请联系管理员")
        if p["allowed_qq_ids"] and body.qq_id not in p["allowed_qq_ids"]:
            fail(403, "user_denied", "此 QQ 尚未获得配对权限")
        count = c.execute("SELECT COUNT(*) FROM devices WHERE qq_id=? AND revoked_at IS NULL", (body.qq_id,)).fetchone()[0]
        if count >= p["max_devices_per_user"]:
            fail(409, "device_limit", "已达到设备数量上限，请先撤销旧设备")
        raw = "97p_" + secrets.token_urlsafe(32)
        code_id = str(uuid.uuid4())
        c.execute("UPDATE codes SET revoked_at=? WHERE qq_id=? AND used_at IS NULL AND revoked_at IS NULL", (now, body.qq_id))
        c.execute("INSERT INTO codes VALUES(?,?,?,?,?,?,?,?,NULL,NULL,NULL)",
                  (code_id, digest("code", raw), "97p_••••" + raw[-6:], body.qq_id, body.group_id, body.nickname, now, now + CODE_TTL))
        audit(c, request, "admin_code_issued" if admin else "code_issued", body.qq_id, body.group_id)
    return {"pairing_id": code_id, "code": raw, "expires_in": CODE_TTL,
            "expires_at": now + CODE_TTL, "single_use": True}


@app.get("/healthz", tags=["public"])
def health():
    with db() as c:
        c.execute("SELECT 1")
    return {"status": "ok", "service": "97launch-auth", "version": "1.5.0"}


@app.post("/api/v1/bot/pairing-codes", tags=["bot"], dependencies=[Depends(bot_auth)])
def bot_issue(body: Issue, request: Request):
    return issue_code(body, request)


@app.post("/api/v1/bot/pairing-codes/{code_id}/revoke", tags=["bot"], dependencies=[Depends(bot_auth)])
def bot_revoke(code_id: uuid.UUID, body: BotRevoke, request: Request):
    return revoke_pairing(code_id, request, body)


@app.post("/api/v1/pairing/redeem", tags=["launcher"])
def redeem(body: Redeem, request: Request):
    rate(request, "redeem-ip", 10, 300)
    raw = "".join(body.code.split())
    if not re.fullmatch(r"97p_[A-Za-z0-9_-]{43}", raw):
        fail(400, "code_invalid", "配对 Token 无效、已使用或已过期")
    now = int(time.time())
    with db() as c:
        c.execute("BEGIN IMMEDIATE")
        code = c.execute("SELECT * FROM codes WHERE code_hash=?", (digest("code", raw),)).fetchone()
        if not code or code["used_at"] or code["revoked_at"] or code["expires_at"] <= now:
            fail(400, "code_invalid", "配对 Token 无效、已使用或已过期")
        p = policy(c)
        if code["group_id"] not in p["allowed_group_ids"] or (p["allowed_qq_ids"] and code["qq_id"] not in p["allowed_qq_ids"]):
            fail(403, "pairing_denied", "此配对已不再符合授权规则")
        installation = str(body.installation_id)
        old = c.execute("SELECT * FROM devices WHERE installation_id=?", (installation,)).fetchone()
        if old and old["qq_id"] != code["qq_id"]:
            fail(409, "installation_conflict", "此安装已绑定其他 QQ，请重新生成安装标识后配对")
        count = c.execute("SELECT COUNT(*) FROM devices WHERE qq_id=? AND revoked_at IS NULL AND installation_id<>?",
                          (code["qq_id"], installation)).fetchone()[0]
        if count >= p["max_devices_per_user"]:
            fail(409, "device_limit", "已达到设备数量上限，请先撤销旧设备")
        token = "97d_" + secrets.token_urlsafe(32)
        device_id = old["id"] if old else str(uuid.uuid4())
        c.execute("""INSERT INTO devices VALUES(?,?,?,?,?,?,?,?,?,?,?,?,NULL)
            ON CONFLICT(installation_id) DO UPDATE SET token_hash=excluded.token_hash,
            nickname=excluded.nickname,group_id=excluded.group_id,name=excluded.name,
            platform=excluded.platform,app_version=excluded.app_version,last_seen_at=excluded.last_seen_at,
            last_ip=excluded.last_ip,revoked_at=NULL""",
                  (device_id, installation, digest("device", token), code["qq_id"], code["nickname"], code["group_id"],
                   body.device_name, body.platform, body.app_version, now, now, ip(request)))
        c.execute("UPDATE codes SET used_at=?,device_id=? WHERE id=?", (now, device_id, code["id"]))
        audit(c, request, "device_paired", device_id, code["qq_id"])
        device = c.execute("SELECT * FROM devices WHERE id=?", (device_id,)).fetchone()
        result = device_public(device)
    return {"device_token": token, "token_type": "Bearer", "expires_at": None, "device": result}


@app.get("/api/v1/devices/me", tags=["launcher"])
def me(device=Depends(device_auth)):
    return {"authorized": True, "device": device_public(device)}


@app.post("/api/v1/devices/heartbeat", tags=["launcher"])
def heartbeat(body: Heartbeat, request: Request, device=Depends(device_auth)):
    now = int(time.time())
    with db() as c:
        updated = c.execute("UPDATE devices SET last_seen_at=?,last_ip=?,app_version=? WHERE id=? AND revoked_at IS NULL",
                            (now, ip(request), body.app_version, device["id"]))
        if not updated.rowcount:
            fail(403, "device_revoked", "此设备授权已撤销")
    return {"authorized": True, "server_time": now, "next_check_in": 900}


@app.post("/api/v1/devices/logout", tags=["launcher"])
def logout_device(request: Request, device=Depends(device_auth)):
    with db() as c:
        c.execute("UPDATE devices SET revoked_at=? WHERE id=?", (int(time.time()), device["id"]))
        audit(c, request, "device_logged_out", device["id"], device["qq_id"])
    return {"revoked": True}


@app.post("/api/admin/login", tags=["admin"])
def admin_login(body: Login, request: Request, response: Response):
    local = local_admin(request)
    origin = LOCAL_ORIGIN if local else ORIGIN
    if (not local and request.url.scheme != "https") or request.headers.get("origin") != origin:
        fail(403, "https_required", "请通过 HTTPS 管理网页登录")
    rate(request, "admin-login", 5, 300)
    if not hmac.compare_digest(digest("admin", body.key), CONFIG["admin_key_hash"]):
        fail(401, "admin_unauthorized", "管理密钥不正确")
    token = secrets.token_urlsafe(32)
    now = int(time.time())
    with db() as c:
        c.execute("DELETE FROM sessions WHERE expires_at <= ?", (now,))
        previous = request.cookies.get(admin_cookie(request))
        if previous:
            c.execute("DELETE FROM sessions WHERE token_hash=?", (digest("admin-session", previous),))
        c.execute("INSERT INTO sessions VALUES(?,?)", (digest("admin-session", token), now + SESSION_TTL))
        audit(c, request, "admin_login")
    response.set_cookie(admin_cookie(request), token, max_age=SESSION_TTL, secure=not local, httponly=True, samesite="strict", path="/")
    return {"authenticated": True, "expires_at": now + SESSION_TTL}


@app.post("/api/admin/logout", dependencies=[Depends(admin_auth)], tags=["admin"])
def admin_logout(request: Request, response: Response):
    with db() as c:
        c.execute("DELETE FROM sessions WHERE token_hash=?", (digest("admin-session", request.cookies.get(admin_cookie(request), "")),))
    response.delete_cookie(admin_cookie(request), path="/", secure=not local_admin(request), httponly=True, samesite="strict")
    return {"authenticated": False}


@app.get("/api/admin/overview", dependencies=[Depends(admin_auth)], tags=["admin"])
def overview():
    now = int(time.time())
    with db() as c:
        counts = {
            "active_devices": c.execute("SELECT COUNT(*) FROM devices WHERE revoked_at IS NULL").fetchone()[0],
            "paired_users": c.execute("SELECT COUNT(DISTINCT qq_id) FROM devices WHERE revoked_at IS NULL").fetchone()[0],
            "pending_codes": c.execute("SELECT COUNT(*) FROM codes WHERE used_at IS NULL AND revoked_at IS NULL AND expires_at>?", (now,)).fetchone()[0],
            "today_pairings": c.execute("SELECT COUNT(*) FROM codes WHERE used_at>=?", (((now + 28800) // 86400) * 86400 - 28800,)).fetchone()[0],
        }
        return {"counts": counts, "policy": policy(c), "server_time": now, "api_base": ORIGIN, "public_ready": PUBLIC_READY,
                "bot_endpoint": ORIGIN + "/api/v1/bot/pairing-codes"}


@app.get("/api/admin/devices", dependencies=[Depends(admin_auth)], tags=["admin"])
def devices(q: str = Query(default="", max_length=80), offset: int = Query(default=0, ge=0)):
    with db() as c:
        where = "WHERE instr(lower(qq_id || ' ' || nickname || ' ' || name),lower(?))>0"
        total = c.execute("SELECT COUNT(*) FROM devices " + where, (q,)).fetchone()[0]
        rows = c.execute("SELECT * FROM devices " + where + " ORDER BY created_at DESC LIMIT 50 OFFSET ?", (q, offset)).fetchall()
    return {"items": [device_public(r) for r in rows], "total": total, "offset": offset, "limit": 50}


@app.get("/api/admin/pairing-codes", dependencies=[Depends(admin_auth)], tags=["admin"])
def codes(offset: int = Query(default=0, ge=0)):
    now = int(time.time())
    with db() as c:
        rows = c.execute("SELECT id,code_hint,qq_id,group_id,nickname,created_at,expires_at,used_at,revoked_at,device_id FROM codes ORDER BY created_at DESC LIMIT 50 OFFSET ?", (offset,)).fetchall()
        total = c.execute("SELECT COUNT(*) FROM codes").fetchone()[0]
    items = []
    for row in rows:
        item = dict(row)
        item["status"] = "used" if row["used_at"] else "revoked" if row["revoked_at"] else "expired" if row["expires_at"] <= now else "pending"
        items.append(item)
    return {"items": items, "total": total, "offset": offset, "limit": 50}


@app.post("/api/admin/pairing-codes", dependencies=[Depends(admin_auth)], tags=["admin"])
def admin_issue(body: Issue, request: Request):
    return issue_code(body, request, admin=True)


@app.get("/api/admin/audit", dependencies=[Depends(admin_auth)], tags=["admin"])
def events(offset: int = Query(default=0, ge=0)):
    with db() as c:
        rows = c.execute("SELECT * FROM audit ORDER BY id DESC LIMIT 50 OFFSET ?", (offset,)).fetchall()
        total = c.execute("SELECT COUNT(*) FROM audit").fetchone()[0]
    return {"items": [dict(r) for r in rows], "total": total, "offset": offset, "limit": 50}


@app.put("/api/admin/policy", dependencies=[Depends(admin_auth)], tags=["admin"])
def save_policy(body: Policy, request: Request):
    value = body.model_dump()
    value["allowed_group_ids"] = list(dict.fromkeys(value["allowed_group_ids"]))
    value["allowed_qq_ids"] = list(dict.fromkeys(value["allowed_qq_ids"]))
    with db() as c:
        c.execute("UPDATE settings SET value=? WHERE key='policy'", (json.dumps(value),))
        audit(c, request, "policy_updated", detail=json.dumps(value))
    return value


@app.post("/api/admin/devices/{device_id}/revoke", dependencies=[Depends(admin_auth)], tags=["admin"])
def revoke_device(device_id: uuid.UUID, request: Request):
    with db() as c:
        c.execute("BEGIN IMMEDIATE")
        row = c.execute("SELECT * FROM devices WHERE id=?", (str(device_id),)).fetchone()
        if not row:
            fail(404, "device_missing", "设备不存在")
        if not row["revoked_at"]:
            c.execute("UPDATE devices SET revoked_at=? WHERE id=?", (int(time.time()), str(device_id)))
            audit(c, request, "device_revoked", str(device_id), row["qq_id"])
    return {"revoked": True}


@app.post("/api/admin/pairing-codes/{code_id}/revoke", dependencies=[Depends(admin_auth)], tags=["admin"])
def revoke_code(code_id: uuid.UUID, request: Request):
    return revoke_pairing(code_id, request)


def revoke_pairing(code_id: uuid.UUID, request: Request, applicant: BotRevoke | None = None):
    with db() as c:
        c.execute("BEGIN IMMEDIATE")
        row = c.execute("SELECT * FROM codes WHERE id=?", (str(code_id),)).fetchone()
        if not row or (applicant and (row["qq_id"] != applicant.qq_id or row["group_id"] != applicant.group_id)):
            fail(404, "code_missing", "配对记录不存在")
        if row["used_at"]:
            fail(409, "code_used", "配对 Token 已使用，请在设备列表中撤销设备")
        if not row["revoked_at"]:
            c.execute("UPDATE codes SET revoked_at=? WHERE id=?", (int(time.time()), str(code_id)))
            audit(c, request, "code_delivery_failed" if applicant else "code_revoked", str(code_id), row["qq_id"])
    return {"revoked": True}


@app.get("/api/admin/openapi.json", dependencies=[Depends(admin_auth)], tags=["admin"])
def schema():
    return app.openapi()


@app.get("/", include_in_schema=False)
def index():
    return FileResponse(ROOT / "static" / "index.html")


from catalog import Catalog

catalog = Catalog(db, fail, audit, digest, ROOT, DB_PATH)
catalog.install_routes(app, admin_auth, device_auth)
from updates import Releases
updates = Releases(catalog, rate, device_auth)
updates.install_routes(app, admin_auth)
app.mount("/static", StaticFiles(directory=ROOT / "static"), name="static")
