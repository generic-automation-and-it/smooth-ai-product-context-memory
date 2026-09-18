#!/usr/bin/env python3
"""Local mTLS terminator that lets SmoothLlmImposter forward to a vllm-proxy endpoint.

The imposter's upstream HttpClient has no client-certificate or custom-CA support, so it
cannot speak to the vllm-proxy directly. This listens for plain HTTP on the loopback
interface and re-issues each request over mTLS using the credential in the profile.

Two headers are rewritten on the way out:

  Host           the server answers 421 Misdirected Request for any other value, so the
                 imposter's own "host.docker.internal:8888" has to be replaced.
  Authorization  set from the profile's apiKey. This is why the imposter is configured with
                 Secret=Dummy: the real key lives only in the mode-600 profile and never
                 appears in a docker run line, a shell history, or the container's env.

Bodies are relayed byte-for-byte -- chunked framing is passed through rather than decoded
and re-encoded -- so SSE streams arrive unbuffered and frame-identical.

Each request gets its own upstream connection and Connection: close. That costs one TLS
handshake per call (~250ms, negligible against LLM latency) and removes every keepalive
framing edge case, which matters more here than the saved round trip.

  VLLM_PROFILE     profile to read           (default: ~/.config/vllm-proxy/profile.json)
  VLLM_PROXY_BIND  interface to listen on    (default: 127.0.0.1)
  VLLM_PROXY_PORT  port to listen on         (default: 8888)
"""
import json
import os
import pathlib
import socket
import socketserver
import ssl
import sys
import time
from urllib.parse import urlsplit

PROFILE = pathlib.Path(os.environ.get("VLLM_PROFILE", pathlib.Path.home() / ".config/vllm-proxy/profile.json"))
BIND = os.environ.get("VLLM_PROXY_BIND", "127.0.0.1")
PORT = int(os.environ.get("VLLM_PROXY_PORT", "8888"))

# Headers dropped from the outbound request. Authorization and Host are re-set from the
# profile; x-api-key is removed outright because the server authenticates on Authorization
# and a stale second credential header only invites ambiguity.
DROP = {"host", "authorization", "x-api-key", "connection", "proxy-connection", "keep-alive"}


def log(msg):
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


def load_profile():
    try:
        p = json.loads(PROFILE.read_text())
    except FileNotFoundError:
        sys.exit(f"no profile at {PROFILE} (set VLLM_PROFILE)")
    except (json.JSONDecodeError, UnicodeDecodeError) as e:
        sys.exit(f"profile is not valid JSON: {e}")

    missing = [k for k in ("name", "baseUrl", "apiKey", "authorityPem", "clientCertPem", "clientKeyPem") if not p.get(k)]
    if missing:
        sys.exit("profile is missing: " + ", ".join(missing))

    # Written every run so a refreshed profile rotates the material with no cache clearing.
    # Shared with claude-vllm, which uses the same layout.
    d = pathlib.Path(os.environ.get("XDG_CACHE_HOME", pathlib.Path.home() / ".cache")) / "vllm-proxy" / p["name"]
    d.mkdir(parents=True, exist_ok=True)
    d.chmod(0o700)
    for key, name in (("authorityPem", "ca.pem"), ("clientCertPem", "client.crt"), ("clientKeyPem", "client.key")):
        f = d / name
        f.write_text(p[key])
        f.chmod(0o600)

    url = urlsplit(p["baseUrl"])
    if url.scheme != "https" or not url.hostname:
        sys.exit(f"baseUrl must be an https URL with a host: {p['baseUrl']}")

    ctx = ssl.create_default_context(cafile=str(d / "ca.pem"))
    ctx.load_cert_chain(certfile=str(d / "client.crt"), keyfile=str(d / "client.key"))
    return {
        "name": p["name"],
        "host": url.hostname,
        "port": url.port or 443,
        # Any path in baseUrl prefixes the request target, so a profile served under a
        # subpath keeps working.
        "prefix": url.path.rstrip("/"),
        "auth": p["apiKey"],
        "ctx": ctx,
    }


def read_head(f):
    """Read up to and including the blank line ending an HTTP head."""
    head = bytearray()
    while b"\r\n\r\n" not in head:
        chunk = f.readline()
        if not chunk:
            return None
        head += chunk
        if len(head) > 128 * 1024:
            raise ValueError("HTTP head too large")
    return bytes(head)


def split_head(head):
    lines = head.split(b"\r\n")
    start = lines[0].decode("latin-1")
    headers = []
    for line in lines[1:]:
        if not line:
            break
        name, _, value = line.partition(b":")
        headers.append((name.decode("latin-1").strip(), value.decode("latin-1").strip()))
    return start, headers


def relay_chunked(src, dst):
    """Pass a chunked body through without decoding it."""
    while True:
        size_line = src.readline()
        if not size_line:
            return
        dst.write(size_line)
        size = int(size_line.split(b";")[0].strip() or b"0", 16)
        if size == 0:
            # Trailers, then the blank line that ends them.
            while True:
                line = src.readline()
                if not line:
                    return
                dst.write(line)
                if line in (b"\r\n", b"\n"):
                    dst.flush()
                    return
        remaining = size + 2  # payload plus its CRLF
        while remaining:
            buf = src.read(min(remaining, 65536))
            if not buf:
                return
            dst.write(buf)
            remaining -= len(buf)
        dst.flush()


def relay_length(src, dst, length):
    while length:
        buf = src.read(min(length, 65536))
        if not buf:
            return
        dst.write(buf)
        dst.flush()
        length -= len(buf)


class Handler(socketserver.StreamRequestHandler):
    timeout = 900
    upstream = None  # set in main()

    def handle(self):
        up = self.upstream
        try:
            head = read_head(self.rfile)
        except (ValueError, OSError):
            return
        if not head:
            return

        start, headers = split_head(head)
        try:
            method, target, _ = start.split(" ", 2)
        except ValueError:
            return

        kept = [(n, v) for n, v in headers if n.lower() not in DROP]
        lower = {n.lower(): v for n, v in headers}

        hostport = up["host"] if up["port"] == 443 else f"{up['host']}:{up['port']}"
        out = [f"{method} {up['prefix']}{target} HTTP/1.1"]
        out.append(f"Host: {hostport}")
        out.append(f"Authorization: Bearer {up['auth']}")
        out.append("Connection: close")
        out += [f"{n}: {v}" for n, v in kept]

        raw = socket.create_connection((up["host"], up["port"]), timeout=60)
        try:
            tls = up["ctx"].wrap_socket(raw, server_hostname=up["host"])
        except (ssl.SSLError, OSError) as e:
            raw.close()
            log(f"{method} {target} -> TLS failed: {e}")
            self.fail(502, f"vllm-tls-proxy: upstream TLS handshake failed: {e}")
            return

        try:
            tls.settimeout(self.timeout)
            wf = tls.makefile("wb")
            wf.write(("\r\n".join(out) + "\r\n\r\n").encode("latin-1"))
            wf.flush()

            if lower.get("transfer-encoding", "").lower().endswith("chunked"):
                relay_chunked(self.rfile, wf)
            elif lower.get("content-length"):
                relay_length(self.rfile, wf, int(lower["content-length"]))
            wf.flush()

            rf = tls.makefile("rb")
            resp = read_head(rf)
            if not resp:
                log(f"{method} {target} -> upstream closed before responding")
                self.fail(502, "vllm-tls-proxy: upstream closed before sending a response")
                return

            status, resp_headers = split_head(resp)
            # The upstream connection closes after this response; say so downstream rather
            # than let HttpClient try to reuse a socket that is already gone.
            emit = [status] + [f"{n}: {v}" for n, v in resp_headers if n.lower() != "connection"]
            emit.append("Connection: close")
            self.wfile.write(("\r\n".join(emit) + "\r\n\r\n").encode("latin-1"))
            self.wfile.flush()
            log(f"{method} {target} -> {status}")

            # Body verbatim, flushed per read so SSE frames are not held back.
            while True:
                buf = rf.read1(65536) if hasattr(rf, "read1") else rf.read(65536)
                if not buf:
                    break
                self.wfile.write(buf)
                self.wfile.flush()
        except (OSError, ssl.SSLError) as e:
            log(f"{method} {target} -> relay error: {e}")
        finally:
            try:
                tls.close()
            except OSError:
                pass

    def fail(self, code, message):
        body = message.encode()
        try:
            self.wfile.write(
                f"HTTP/1.1 {code} Bad Gateway\r\nContent-Type: text/plain\r\n"
                f"Content-Length: {len(body)}\r\nConnection: close\r\n\r\n".encode("latin-1")
            )
            self.wfile.write(body)
            self.wfile.flush()
        except OSError:
            pass


class Server(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = True


def main():
    up = load_profile()
    Handler.upstream = up
    with Server((BIND, PORT), Handler) as srv:
        log(f"vllm-tls-proxy: {BIND}:{PORT} -> https://{up['host']}:{up['port']}{up['prefix']} as {up['name']}")
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            log("stopping")


if __name__ == "__main__":
    main()
