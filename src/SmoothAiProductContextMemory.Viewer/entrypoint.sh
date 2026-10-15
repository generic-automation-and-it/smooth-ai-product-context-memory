#!/bin/sh
set -eu

# The Python bridges are Caddy sidecars. Caddy is PID 1 and owns them as
# children. Two processes: P4a (read-only, write-token absent) on 5181 and
# P4b (write, write-token required) on 5182. Process isolation is the
# strongest NFR-02 enforcement — the write token is physically absent from
# P4a and physically required by P4b. The capture client and each bridge
# carry their own import-time guards as defense in depth.
python3 /bridge/bridge.py &
python3 /bridge/bridge_p4b.py &
exec caddy run --config /etc/caddy/Caddyfile --adapter caddyfile
