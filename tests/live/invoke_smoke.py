"""Live test of civil3d_invoke (C154) through a separate stdio instance of the Node server (build/index.js) against the running plugin.
Changes nothing in the drawing: the write test carries expectDrawing for a drawing that is not open and must be rejected by the plugin.
    python -I tests/live/invoke_smoke.py
No arguments or environment variables are needed.
EADDRINUSE on the bridge port 3000 is expected when another MCP client's instance is already running."""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class Server:
    def __init__(self):
        self.p = subprocess.Popen(["node", str(ROOT / "build" / "index.js")], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
        self.n = 0
        self.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "invoke_smoke", "version": "1"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")

    def call(self, method, params):
        self.n += 1
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params}) + "\n")
        self.p.stdin.flush()
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("the server did not reply")
            msg = json.loads(line)
            if msg.get("id") == self.n:
                return msg

    def tool(self, name, args):
        r = self.call("tools/call", {"name": name, "arguments": args})["result"]
        text = "".join(c.get("text", "") for c in r.get("content", []))
        try:
            data = json.loads(text)
        except ValueError:
            data = text
        return bool(r.get("isError")), data, text

    def close(self):
        self.p.terminate()


failures = 0


def check(name, ok, info=""):
    global failures
    failures += 0 if ok else 1
    print(f"{'OK ' if ok else 'ERR'} {name} {str(info)[:220]}")


def main():
    s = Server()
    try:
        tools = [t["name"] for t in s.call("tools/list", {})["result"]["tools"]]
        check("civil3d_invoke exists", "civil3d_invoke" in tools, len(tools))
        err, data, text = s.tool("civil3d_invoke", {"action": "list", "contains": "Viewport"})
        check("list", not err and "setViewport" in text, text[:200])
        err, data, text = s.tool("civil3d_invoke", {"action": "read", "method": "listLayouts"})
        check("read listLayouts without approval", not err and "layouts" in text, text[:160])
        err, data, text = s.tool("civil3d_invoke", {"action": "read", "method": "createCircle", "params": {"x": 0, "y": 0, "radius": 1}})
        check("read of a write command is rejected", err and "action write" in text, text[:200])
        err, data, text = s.tool("civil3d_invoke", {"action": "read", "method": "doesNotExist"})
        check("unknown method is rejected", err and "does not exist" in text, text[:200])
        args = {"action": "write", "method": "createCircle", "params": {"x": 0, "y": 0, "radius": 1, "expectDrawing": "Does_not_exist_C154.dwg"}}
        err, data, text = s.tool("civil3d_invoke", args)
        check("write requires approval", err and "approval" in text.lower(), text[:200])
        err, data, text = s.tool("civil3d_request_approval", {"toolName": "civil3d_invoke", "action": "write", "parameters": args})
        token = data.get("approvalToken") if isinstance(data, dict) else None
        check("approval issued", bool(token), text[:160])
        err, data, text = s.tool("civil3d_invoke", {**args, "approvalToken": token})
        check("write reaches the plugin (wrong drawing rejected there)", err and "WRONG_DRAWING" in text or "expectDrawing" in text, text[:220])
    finally:
        s.close()
    print(f"\n{failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
