"""G-Helper local API demo: query status, switch performance mode, then restore it.

Usage:  python ghelper_api_demo.py [silent|balanced|turbo|<mode id>]

curl equivalents:
  curl http://127.0.0.1:5110/          (self-description: endpoints, params, side effects, actions)
  curl http://127.0.0.1:5110/status
  curl -X POST "http://127.0.0.1:5110/mode?value=turbo"
  curl -X POST "http://127.0.0.1:5110/gpu?value=standard"     (eco / optimized need &force=1)
  curl -X POST "http://127.0.0.1:5110/charge?value=80"        (40..100)
  curl -X POST "http://127.0.0.1:5110/screen?hz=max&overdrive=1"
  curl -X POST "http://127.0.0.1:5110/backlight?value=2"      (0..3)
  curl -X POST "http://127.0.0.1:5110/aura?value=0"           (ids in /status keyboard.auraModes)
  curl -X POST "http://127.0.0.1:5110/action?value=micmute"   (hotkey actions listed in GET /)
Port comes from "api_port" in %AppData%\\GHelper\\config.json (default 5110, 0 = disabled).
"""
import json
import sys
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:5110"
sys.stdout.reconfigure(encoding="utf-8")


def call(path, method="GET", headers=None):
    req = urllib.request.Request(BASE + path, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        return e.code, json.load(e)


def show(title, status):
    print(f"--- {title} ---")
    print(json.dumps(status, ensure_ascii=False, indent=2))


target = sys.argv[1] if len(sys.argv) > 1 else "turbo"

code, help_ = call("/")
assert code == 200 and "micmute" in help_["actions"], help_
print(f"{len(help_['endpoints'])} endpoints, {len(help_['actions'])} actions")

code, before = call("/status")
assert code == 200, before
show("status", before)

code, res = call(f"/mode?value={target}", "POST")
assert code == 200, res
time.sleep(2)
code, after = call("/status")
print(f"\nmode: {before['mode']['name']} -> {after['mode']['name']}")

# safety checks: browser-origin requests and bad values are rejected, mutations need POST
assert call("/mode?value=turbo", "POST", {"Origin": "http://evil.example"})[0] == 403
assert call("/gpu?value=ultimate", "POST")[0] in (400, 409)
assert call("/gpu?value=eco", "POST")[0] in (400, 409)  # eco without force=1 is refused
assert call("/action?value=custom", "POST")[0] == 400
assert call("/mode?value=turbo")[0] == 405

code, res = call(f"/mode?value={before['mode']['id']}", "POST")
assert code == 200, res
print(f"restored: {before['mode']['name']}\nOK")
