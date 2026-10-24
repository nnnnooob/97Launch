"""Daily SQLite snapshot and retention, invoked by systemd as root."""
import os
import sqlite3
import time
from pathlib import Path

os.umask(0o077)
source = Path('/var/lib/97launch-auth/auth.sqlite3')
directory = Path('/var/backups/97launch-auth')
directory.mkdir(mode=0o700, parents=True, exist_ok=True)
now = int(time.time())
with sqlite3.connect(source) as db:
    with sqlite3.connect(directory / time.strftime('auth-%Y%m%d-%H%M%S.sqlite3', time.gmtime(now))) as target:
        db.backup(target)
    db.execute('DELETE FROM audit WHERE created_at < ?', (now - 90 * 86400,))
    db.execute('DELETE FROM codes WHERE expires_at < ?', (now - 90 * 86400,))
    db.execute('DELETE FROM sessions WHERE expires_at < ?', (now,))
    db.execute('DELETE FROM rates WHERE expires_at < ?', (now,))
for backup in directory.glob('auth-*.sqlite3'):
    if backup.stat().st_mtime < now - 14 * 86400:
        backup.unlink()
