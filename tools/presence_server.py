#!/usr/bin/env python3
"""How many launchers are open now, and how many different players there have been. Aptabase keeps no read API for an
app key, so the count needs a server of its own.

GET /online?session=<random id> marks that launcher as open and answers {"online": n, "total": m}. Launchers ask every
30 seconds; one silent for 90 seconds no longer counts (a crash). Adding &leave=1 removes it at once (a normal close).

total counts each IP address once, for good. Only a salted SHA-256 of the address is stored, in presence_seen.txt
beside this file, with the salt in presence_salt.txt. Nothing else is stored or logged. Behind Cloudflare the address
comes from CF-Connecting-IP; the server listens on 127.0.0.1, so only the tunnel can set that header.

Run it behind HTTPS on your server, then put the address in keys.local.props:
    <JacintoPresenceUrl>https://presence.example.com/online</JacintoPresenceUrl>

Self-check: python presence_server.py --test
"""
import hashlib
import json
import os
import secrets
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

TIMEOUT = 90
MAX_SESSIONS = 200_000  # Enough for this game; a flood of made-up ids must not fill the memory.
MAX_SEEN = 2_000_000
sessions = {}
seen = set()
salt = ""
seen_path = ""
lock = threading.Lock()


def load(folder):
    """Reads what earlier runs stored. The salt never changes, or everyone would count again."""
    global salt, seen_path
    salt_path = os.path.join(folder, "presence_salt.txt")
    seen_path = os.path.join(folder, "presence_seen.txt")
    if not os.path.exists(salt_path):
        descriptor = os.open(salt_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, "w") as file:
            file.write(secrets.token_hex(32))
    with open(salt_path) as file:
        salt = file.read().strip()
    seen.clear()
    if os.path.exists(seen_path):
        with open(seen_path) as file:
            seen.update(line.strip() for line in file if line.strip())


def remember(address):
    if not address:
        return
    digest = hashlib.sha256((salt + address).encode()).hexdigest()[:32]
    if digest not in seen and len(seen) < MAX_SEEN:
        seen.add(digest)
        with open(seen_path, "a") as file:
            file.write(digest + "\n")
            file.flush()
            os.fsync(file.fileno())  # On the disk before the answer, so a power cut loses nobody.


def touch(session, now, address="", leave=False):
    with lock:
        for key in [k for k, last in sessions.items() if now - last > TIMEOUT]:
            del sessions[key]
        if leave:
            sessions.pop(session, None)
        elif session and (session in sessions or len(sessions) < MAX_SESSIONS):
            sessions[session] = now
            remember(address)
        return {"online": len(sessions), "total": len(seen)}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        url = urlparse(self.path)
        query = parse_qs(url.query)
        session = query.get("session", [""])[0][:40]
        if url.path != "/online":
            self.send_error(404)
            return
        address = self.headers.get("CF-Connecting-IP") or self.client_address[0]
        body = json.dumps(touch(session, time.time(), address, "leave" in query)).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


if __name__ == "__main__":
    if "--test" in sys.argv:
        with tempfile.TemporaryDirectory() as folder:
            load(folder)
            assert touch("a", 1000, "1.1.1.1") == {"online": 1, "total": 1}
            assert touch("b", 1010, "2.2.2.2") == {"online": 2, "total": 2}
            assert touch("c", 1020, "1.1.1.1") == {"online": 3, "total": 2}, "the same PC opening again is not a new player"
            assert touch("", 1030, "3.3.3.3")["total"] == 2, "a read without an id counts nobody new"
            assert touch("c", 1040, "1.1.1.1", leave=True) == {"online": 2, "total": 2}, "a close drops out at once"
            assert touch("x", 1050, "4.4.4.4", leave=True)["total"] == 2, "leaving does not count anyone"
            assert touch("a", 1085, "1.1.1.1")["online"] == 2 and touch("a", 1101, "1.1.1.1")["online"] == 1, "a silent launcher drops out"
            before = salt
            load(folder)
            assert salt == before and len(seen) == 2, "the total survives a restart"
            assert "1.1.1.1" not in open(seen_path).read(), "addresses are not stored"
        print("ok")
    else:
        load(os.path.dirname(os.path.abspath(__file__)))
        ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1]) if len(sys.argv) > 1 else 8787), Handler).serve_forever()
