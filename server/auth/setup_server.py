"""Run as root on first deployment. Never prints credentials."""
import hashlib
import hmac
import json
import os
import secrets
from pathlib import Path

origin = os.environ.get('AUTH_ORIGIN', 'http://127.0.0.1:18797').rstrip('/')

directory = Path('/etc/97launch-auth')
directory.mkdir(mode=0o750, exist_ok=True)
path = directory / 'secrets.json'
if path.exists():
    print('Existing credentials preserved.')
else:
    pepper = secrets.token_hex(32)
    admin = '97a_' + secrets.token_urlsafe(32)
    bot = '97b_' + secrets.token_urlsafe(32)
    def hashed(kind, value):
        return hmac.new(pepper.encode(), f'{kind}:{value}'.encode(), hashlib.sha256).hexdigest()
    path.write_text(json.dumps({'hash_key':pepper, 'admin_key_hash':hashed('admin', admin),
                                'bot_key_hash':hashed('bot', bot)}, indent=2))
    os.chmod(path, 0o640)
    access = Path('/root/97launch-auth-access.json')
    access.write_text(json.dumps({'admin_url':origin, 'admin_key':admin,
                                 'bot_api_base':origin, 'bot_key':bot}, indent=2))
    os.chmod(access, 0o600)
    print('Created server credentials; raw keys are in /root/97launch-auth-access.json (0600).')
