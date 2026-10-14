#!/bin/sh
set -eu

# The Python bridge is a Caddy sidecar; it answers /bridge/*. In Phase A it is a
# stub and never loads the write token or touches the store (NFR-02). Caddy is
# PID 1 and owns the bridge as a child.
python3 /bridge/bridge.py &
exec caddy run --config /etc/caddy/Caddyfile --adapter caddyfile
