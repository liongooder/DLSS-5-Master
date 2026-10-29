"""Audit checks for the community server's per-connection limits (DEF-008..010), against a LOCAL wrangler dev only."""
import base64, json, secrets, sys, urllib.request, urllib.error

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8799"
assert BASE.startswith("http://127.0.0.1"), "local server only"
passed = failed = 0


def call(method, path, body=None, install=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if install:
        req.add_header("X-Install", install)
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"null")
        except Exception:
            return e.code, None


def check(name, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
    else:
        failed += 1
        print("FAIL", name, detail)


def new_install(nick=True):
    i = secrets.token_hex(16)
    if nick:
        s, b = call("PUT", "/v1/me", {"install": i, "nickname": "audit" + i[:8]})
        assert s == 200, (s, b)
    return i


run = secrets.token_hex(3)
check("health", call("GET", "/v1/health")[0] == 200)

# DEF-009: one connection may rate a game at most 3 times an hour, whatever the install IDs.
key = f"name:audit{run}"
codes = [call("POST", "/v1/reports", {"install": new_install(False), "key": key, "name": "Audit game", "rating": "works",
                                       "route": "native", "gpu": "RTX 4090", "gpuFamily": "RTX 40"})[0] for _ in range(5)]
check("reports: 3 per game per connection, then 429", codes[:3] == [200, 200, 200] and codes[3:] == [429, 429], codes)
s, game = call("GET", "/v1/games/" + key)
check("reports: only 3 counted", s == 200 and game["total"] == 3, (s, game and game.get("total")))
other = f"name:auditother{run}"
s, _ = call("POST", "/v1/reports", {"install": new_install(False), "key": other, "name": "Other game", "rating": "broken", "route": "feeder"})
check("reports: another game still accepted", s == 200, s)

# DEF-008: flagging needs a nickname; three nicknamed installs still hide a note (normal moderation keeps working).
author = new_install(False)
call("POST", "/v1/reports", {"install": author, "key": f"name:auditnote{run}", "name": "Note game", "rating": "partly",
                             "route": "multipass", "note": "audit note"})
s, game = call("GET", f"/v1/games/name:auditnote{run}")
note_id = game["notes"][0]["id"]
s, b = call("POST", f"/v1/reports/{note_id}/flag", {"install": new_install(False)})
check("flag: an install without a nickname is refused", s == 403, (s, b))
for _ in range(3):
    s, b = call("POST", f"/v1/reports/{note_id}/flag", {"install": new_install(True)})
    check("flag: nicknamed install accepted", s == 200, (s, b))
s, game = call("GET", f"/v1/games/name:auditnote{run}")
check("flag: three flags hide the note", s == 200 and not any(n["id"] == note_id for n in game["notes"]), game)
codes = [call("POST", f"/v1/reports/{note_id}/flag", {"install": new_install(True)})[0] for _ in range(8)]
check("flag: at most 10 per connection an hour", codes.count(429) >= 1 and codes[:6] == [200] * 6, codes)

# DEF-010: one connection may store at most 20 pictures an hour, whatever the install IDs.
jpeg = base64.b64encode(bytes([0xFF, 0xD8, 0xFF, 0xE0]) + secrets.token_bytes(64)).decode()
codes = [call("POST", "/v1/private", {"install": new_install(False), "text": "audit", "image": jpeg})[0] for _ in range(22)]
check("pictures: 20 per connection an hour, then 429", codes[:20] == [200] * 20 and codes[20:] == [429, 429], codes)
s, _ = call("POST", "/v1/private", {"install": new_install(False), "text": "text only still fine"})
check("pictures: a text-only private message still goes through", s == 200, s)

print(f"{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
