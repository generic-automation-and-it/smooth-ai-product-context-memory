#!/bin/sh
set -eu

# The Python bridges are Caddy sidecars. Caddy is PID 1 and owns them as
# children. Two processes: P4a (read-side, never drives a write) on 5181 and
# P4b (write-side) on 5182. No token is ambient (NFR-02): the Console session
# supplies its bearer token per request and the bridges forward it to the
# Host, which enforces the capability.
python3 /bridge/bridge.py &
python3 /bridge/bridge_p4b.py &
exec caddy run --config /etc/caddy/Caddyfile --adapter caddyfile
