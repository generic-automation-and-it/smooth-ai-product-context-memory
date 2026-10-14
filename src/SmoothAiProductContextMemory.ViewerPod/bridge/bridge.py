# Mímisbrunnr Console bridge — Phase A stub.
#
# Serves the /bridge/* surface that the viewer pod's Caddy reverse-proxies to.
# Deliberately small and stdlib-only for Phase A; the real driver of the capture
# pipeline lands in P4 (LADR-09 imports the capture client rather than
# reimplementing its gates). This process never loads or reads the write token
# and never touches the store (NFR-02).
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
PORT = 5181


class Handler(BaseHTTPRequestHandler):
    def _reply(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/bridge/health":
            self._reply(200, {"status": "ok", "phase": "stub"})
        else:
            self._reply(
                200,
                {
                    "phase": "stub",
                    "route": self.path,
                    "note": "review queue and approve land in P4",
                },
            )

    do_POST = do_GET

    def log_message(self, fmt, *args):
        # Keep bridge logging minimal: outcomes only. Never echo a request or a
        # token (NFR-04).
        return


def main():
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
