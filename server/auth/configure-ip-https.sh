#!/bin/bash
set -euo pipefail
umask 077
: "${AUTH_PUBLIC_IP:?Set AUTH_PUBLIC_IP to the public IPv4 address}"
python3 - "$AUTH_PUBLIC_IP" <<'VALIDATE'
import ipaddress, sys
ipaddress.IPv4Address(sys.argv[1])
VALIDATE
APP_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

tls=/etc/97launch-auth/tls
backup=/root/97launch-backups/ip-https-$(date +%Y%m%d-%H%M%S)
mkdir -p "$backup" "$tls"
printf 'AUTH_PUBLIC_IP=%q\n' "$AUTH_PUBLIC_IP" > "$tls/endpoint.env"
cp -a /etc/nginx/sites-available/97launch-auth /etc/systemd/system/97launch-auth.service "$backup/"
python3 - "$backup/auth.sqlite3" <<'PY'
import sqlite3, sys
with sqlite3.connect('/var/lib/97launch-auth/auth.sqlite3') as source, sqlite3.connect(sys.argv[1]) as target:
    source.backup(target)
PY
if [ ! -f "$tls/ca.key" ]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$tls/ca.key" 2>/dev/null
  openssl req -x509 -new -sha256 -days 3650 -key "$tls/ca.key" -out "$tls/ca.pem" \
    -subj '/CN=97Launch Authorization Root CA' \
    -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' \
    -addext 'keyUsage=critical,keyCertSign,cRLSign'
fi
cat > /usr/local/sbin/97launch-renew-tls <<'RENEW'
#!/bin/bash
set -euo pipefail
umask 077
cd /etc/97launch-auth/tls
. ./endpoint.env
if [ -f server.pem ] && openssl x509 -checkend 2592000 -noout -in server.pem >/dev/null &&
   openssl verify -CAfile ca.pem -verify_ip "$AUTH_PUBLIC_IP" -purpose sslserver server.pem >/dev/null 2>&1; then exit 0; fi
trap 'rm -f server.csr server.ext server.new.pem' EXIT
if [ ! -f server.key ]; then openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out server.key 2>/dev/null; fi
openssl req -new -key server.key -out server.csr -subj "/CN=$AUTH_PUBLIC_IP"
printf '%s\n' 'basicConstraints=critical,CA:FALSE' 'keyUsage=critical,digitalSignature,keyEncipherment' 'extendedKeyUsage=serverAuth' "subjectAltName=IP:$AUTH_PUBLIC_IP" > server.ext
openssl x509 -req -sha256 -days 365 -in server.csr -CA ca.pem -CAkey ca.key -CAcreateserial -extfile server.ext -out server.new.pem
openssl verify -CAfile ca.pem -verify_ip "$AUTH_PUBLIC_IP" -purpose sslserver server.new.pem
mv server.new.pem server.pem
nginx -t
systemctl reload nginx
RENEW
chmod 700 /usr/local/sbin/97launch-renew-tls
/usr/local/sbin/97launch-renew-tls
sed "s/auth.example.com/$AUTH_PUBLIC_IP/g" "$APP_DIR/nginx-https.conf.example" > /etc/nginx/sites-available/97launch-auth-ip-https
ln -sfn /etc/nginx/sites-available/97launch-auth-ip-https /etc/nginx/sites-enabled/97launch-auth-ip-https
nginx -t
systemctl reload nginx
mkdir -p /etc/systemd/system/97launch-auth.service.d
printf 'AUTH_ORIGIN=https://%s:18443\n' "$AUTH_PUBLIC_IP" > /etc/97launch-auth/deployment.env
chmod 640 /etc/97launch-auth/deployment.env
chown root:launch-auth /etc/97launch-auth/deployment.env
printf '[Service]\nEnvironmentFile=/etc/97launch-auth/deployment.env\nEnvironment=AUTH_PUBLIC_READY=true\n' > /etc/systemd/system/97launch-auth.service.d/https.conf
cat > /etc/systemd/system/97launch-renew-tls.service <<'SERVICE'
[Unit]
Description=Renew 97Launch private IP TLS certificate
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/97launch-renew-tls
SERVICE
cat > /etc/systemd/system/97launch-renew-tls.timer <<'TIMER'
[Unit]
Description=Daily 97Launch TLS renewal check
[Timer]
OnCalendar=daily
RandomizedDelaySec=1h
Persistent=true
[Install]
WantedBy=timers.target
TIMER
systemctl daemon-reload
systemctl enable --now 97launch-renew-tls.timer
systemctl restart 97launch-auth
if command -v ufw >/dev/null && ufw status | grep -q 'Status: active'; then ufw allow 18443/tcp; fi
printf 'Backup: %s\n' "$backup"
openssl x509 -in "$tls/server.pem" -noout -dates -ext subjectAltName
