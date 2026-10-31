"""Checks for every report on a game's page with the PC it ran on (0.9.8), against a LOCAL wrangler dev only.

Run: npx wrangler dev --local --port 8799 (with schema.sql, schema-v2/3/4.sql applied to the local database), then
python tests/reports_test.py [http://127.0.0.1:8799]. Uses up to 6 flags of the 10 a connection may send an hour.
"""
import json, pathlib, secrets, sys, urllib.request, urllib.error

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8799"
assert BASE.startswith("http://127.0.0.1"), "local server only"
passed = failed = 0


def call(method, path, body=None, install=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if install:
        req.add_header("X-Install", install)
    if token:
        req.add_header("Authorization", "Bearer " + token)
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
        s, b = call("PUT", "/v1/me", {"install": i, "nickname": "spec" + i[:8]})
        assert s == 200, (s, b)
    return i


def admin_token():
    for line in (pathlib.Path(__file__).parent.parent / ".dev.vars").read_text().splitlines():
        if line.startswith("ADMIN_TOKEN="):
            return line.split("=", 1)[1].strip().strip('"')
    return None


PC = {"gpu": "NVIDIA GeForce RTX 4070", "gpuFamily": "RTX 40", "driver": "581.57", "cpu": "AMD Ryzen 7 7800X3D", "ramGb": 32,
      "os": "Windows 11 24H2 (26100)", "api": "dx12", "appVersion": "0.9.8", "versions": {"renodx-dlss5": "10.8.0"}}
run = secrets.token_hex(3)


def report(install, key, **fields):
    body = {"install": install, "key": key, "name": "Spec game", "rating": "broken", "route": "native", "mfg": True}
    body.update(fields)
    return call("POST", "/v1/reports", body)


# A report with its PC, and no note: the page lists it with everything; "notes" (read by apps before 0.9.8) stays empty.
key = f"name:specfull{run}"
author = new_install(False)
s, _ = report(author, key, **PC)
check("full: accepted", s == 200, s)
s, game = call("GET", f"/v1/games/{key}", install=author)
rows = game["reports"] if s == 200 else []
row = rows[0] if rows else {}
check("full: one report listed", len(rows) == 1, game)
check("full: its PC", (row.get("gpu"), row.get("driver"), row.get("cpu"), row.get("ram_gb"), row.get("os"))
      == ("NVIDIA GeForce RTX 4070", "581.57", "AMD Ryzen 7 7800X3D", 32, "Windows 11 24H2 (26100)"), row)
check("full: route, API, versions, app", (row.get("route"), row.get("mfg"), row.get("api"), row.get("versions"), row.get("app_version"), row.get("hidden"))
      == ("native", True, "dx12", {"renodx-dlss5": "10.8.0"}, "0.9.8", False), row)
check("full: no note, so nothing in notes", game.get("notes") == [], game.get("notes"))
check("full: the author's own report is marked", game.get("mine") and game["mine"]["id"] == row.get("id"), game.get("mine"))
check("full: no install hash in the page", "install" not in row and "author" not in row, row)

# Sending again replaces the report (one per install per game) with the new PC.
s, _ = report(author, key, **{**PC, "driver": "590.12", "ramGb": 64})
s, game = call("GET", f"/v1/games/{key}")
check("update: still one report, new driver and memory", s == 200 and len(game["reports"]) == 1
      and game["reports"][0]["driver"] == "590.12" and game["reports"][0]["ram_gb"] == 64, game)

# Values that do not look right are stored empty; the report itself is never refused for them.
key = f"name:specodd{run}"
s, b = report(new_install(False), key, gpu="NVIDIA GeForce RTX 5090", cpu="<b>bold</b>", os="Windows XP", driver="latest",
              ramGb=99999, appVersion="free text", api="dx99")
check("odd: accepted", s == 200, (s, b))
s, game = call("GET", f"/v1/games/{key}")
row = game["reports"][0] if s == 200 and game["reports"] else {}
check("odd: stored empty", (row.get("cpu"), row.get("os"), row.get("driver"), row.get("ram_gb"), row.get("app_version"), row.get("api"))
      == ("", "", "", 0, "", ""), row)
for bad in ["32", 31.5, -4, True]:
    k = f"name:specram{run}{secrets.token_hex(2)}"
    report(new_install(False), k, ramGb=bad)
    s, game = call("GET", f"/v1/games/{k}")
    check(f"odd: memory {bad!r} stored as 0", s == 200 and game["reports"][0]["ram_gb"] == 0, game)

# A report from an app before 0.9.8 (no PC beyond the card) still goes in.
key = f"name:specold{run}"
s, _ = call("POST", "/v1/reports", {"install": new_install(False), "key": key, "name": "Old app game", "rating": "works", "route": "feeder",
                                    "gpu": "NVIDIA GeForce RTX 3080", "gpuFamily": "RTX 30", "note": "fine here", "appVersion": "0.9.7"})
check("old app: accepted", s == 200, s)
s, game = call("GET", f"/v1/games/{key}")
row = game["reports"][0] if s == 200 and game["reports"] else {}
check("old app: listed without the new values", (row.get("gpu"), row.get("cpu"), row.get("ram_gb"), row.get("note"))
      == ("NVIDIA GeForce RTX 3080", "", 0, "fine here"), row)
check("old app: its note still in notes", [n["note"] for n in game.get("notes", [])] == ["fine here"], game.get("notes"))

# Three flags hide a report's text: the note, the card's and processor's names, the versions and the nickname go;
# the rating, route and the checked values stay.
key = f"name:spechide{run}"
report(new_install(False), key, **PC)   # an older report that stays visible
writer = new_install(True)
report(writer, key, note="rude words", **PC)
s, game = call("GET", f"/v1/games/{key}")
rid = next(r["id"] for r in game["reports"] if r["note"] == "rude words")
check("hide: newest first", game["reports"][0]["id"] == rid, [r["id"] for r in game["reports"]])
check("hide: nickname shown before", game["reports"][0]["nickname"] == "spec" + writer[:8], game["reports"][0])
for _ in range(3):
    call("POST", f"/v1/reports/{rid}/flag", {"install": new_install(True)})
s, game = call("GET", f"/v1/games/{key}")
check("hide: a hidden report goes after the visible ones", s == 200 and [r["id"] for r in game["reports"]][-1] == rid,
      [(r["id"], r["hidden"]) for r in game.get("reports", [])])
row = next((r for r in game.get("reports", []) if r["id"] == rid), {})
check("hide: text gone", (row.get("hidden"), row.get("note"), row.get("gpu"), row.get("cpu"), row.get("versions"), row.get("nickname"))
      == (True, "", "", "", {}, None), row)
check("hide: rating, route and checked values kept", (row.get("rating"), row.get("route"), row.get("gpu_family"), row.get("driver"), row.get("ram_gb"))
      == ("broken", "native", "RTX 40", "581.57", 32), row)
check("hide: not in notes", game.get("notes") == [], game.get("notes"))

# A report without a note can be flagged too (for a fake card or processor name); the developer sees it with its PC.
key = f"name:specflag{run}"
report(new_install(False), key, **{**PC, "cpu": "Fake CPU"})
s, game = call("GET", f"/v1/games/{key}")
rid = game["reports"][0]["id"]
codes = [call("POST", f"/v1/reports/{rid}/flag", {"install": new_install(True)})[0] for _ in range(3)]
check("flag without note: accepted", codes == [200, 200, 200], codes)
s, game = call("GET", f"/v1/games/{key}")
check("flag without note: hidden", s == 200 and game["reports"][0]["hidden"] is True and game["reports"][0]["cpu"] == "", game)
token = admin_token()
if token:
    s, flagged = call("GET", "/v1/admin/flagged", token=token)
    mine = [f for f in flagged or [] if f["id"] == rid]
    check("moderator: flagged report with its route and PC", s == 200 and mine and (mine[0]["route"], mine[0]["cpu"], mine[0]["ram_gb"], mine[0]["os"])
          == ("native", "Fake CPU", 32, "Windows 11 24H2 (26100)"), (s, mine))
else:
    print("SKIP moderator check: no ADMIN_TOKEN in .dev.vars")

# The games list still counts them.
s, games = call("GET", f"/v1/games?q=spec+game")
check("list: still works", s == 200 and games["total"] >= 1, (s, games and games.get("total")))

print(f"{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
