#!/bin/sh
set -eu
umask 027
: "${AUTH_ORIGIN:?Set AUTH_ORIGIN to your public HTTPS origin before deployment}"
export AUTH_ORIGIN
APP_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
case "$APP_DIR" in /opt/97launch-auth/releases/*) ;; *) echo 'Deploy from /opt/97launch-auth/releases/<version>'; exit 1;; esac
if ! id launch-auth >/dev/null 2>&1; then useradd --system --home /var/lib/97launch-auth --shell /usr/sbin/nologin launch-auth; fi
install -d -o launch-auth -g launch-auth -m 750 /var/lib/97launch-auth
install -d -o root -g launch-auth -m 750 /etc/97launch-auth
printf 'AUTH_ORIGIN=%s\n' "$AUTH_ORIGIN" > /etc/97launch-auth/deployment.env
chmod 640 /etc/97launch-auth/deployment.env
chown root:launch-auth /etc/97launch-auth/deployment.env
python3 "$APP_DIR/setup_server.py"
chown root:launch-auth /etc/97launch-auth/secrets.json
chmod 640 /etc/97launch-auth/secrets.json
python3 -m venv /opt/97launch-auth/venv
/opt/97launch-auth/venv/bin/pip install --disable-pip-version-check -r "$APP_DIR/requirements.txt"
/opt/97launch-auth/venv/bin/pip freeze > "$APP_DIR/requirements.lock"
chgrp -R launch-auth /opt/97launch-auth/venv
chmod -R o=rX "$APP_DIR"
ln -sfn "$APP_DIR" /opt/97launch-auth/current
cat > /etc/systemd/system/97launch-auth.service <<'UNIT'
[Unit]
Description=97Launch pairing authorization API
After=network.target

[Service]
User=launch-auth
Group=launch-auth
WorkingDirectory=/opt/97launch-auth/current
EnvironmentFile=/etc/97launch-auth/deployment.env
Environment=AUTH_LOCAL_ADMIN_ORIGIN=http://127.0.0.1:19797
Environment=AUTH_SECRETS=/etc/97launch-auth/secrets.json
Environment=AUTH_DATABASE=/var/lib/97launch-auth/auth.sqlite3
Environment=PYTHONDONTWRITEBYTECODE=1
ExecStart=/opt/97launch-auth/venv/bin/uvicorn app:app --host 127.0.0.1 --port 18797 --workers 1 --proxy-headers --forwarded-allow-ips 127.0.0.1 --no-access-log
Restart=on-failure
RestartSec=3
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/97launch-auth
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
CapabilityBoundingSet=
MemoryMax=512M
TasksMax=64

[Install]
WantedBy=multi-user.target
UNIT
cat > /etc/systemd/system/97launch-auth-maintenance.service <<'UNIT'
[Unit]
Description=97Launch authorization database backup and retention
[Service]
Type=oneshot
ExecStart=/usr/bin/python3 /opt/97launch-auth/current/maintenance.py
UMask=0077
UNIT
cat > /etc/systemd/system/97launch-auth-maintenance.timer <<'UNIT'
[Unit]
Description=Daily 97Launch authorization backup
[Timer]
OnCalendar=*-*-* 04:10:00
Persistent=true
[Install]
WantedBy=timers.target
UNIT
systemctl daemon-reload
systemctl enable --now 97launch-auth.service 97launch-auth-maintenance.timer
systemctl restart 97launch-auth.service
echo 'Authorization service installed; configure HTTPS before use.'
