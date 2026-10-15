# syntax=docker/dockerfile:1
#
# Viewer pod: the loopback Mímisbrunnr Console. One container runs Caddy
# (serves the static SPA and proxies /api + /bridge), the Python bridge, and
# the committed no-build SPA module. The Host image stays pure (LADR-01).
#
# The supervisor is the entrypoint script: it starts the bridge then execs
# Caddy, so Caddy is PID 1 and owns the bridge as a child — no extra init.

FROM caddy:2.8-alpine AS runtime

# Python 3 for the bridge. Standard library only in Phase A; P4 adds no pip
# dependency because it imports the capture client as a library (LADR-09).
RUN apk add --no-cache python3 \
    && addgroup -S caddy \
    && adduser -S caddy -G caddy

COPY Caddyfile /etc/caddy/Caddyfile
COPY entrypoint.sh /entrypoint.sh
COPY wwwroot/ /srv/spa/
COPY bridge/ /bridge/

ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/generic-automation-and-it/smooth-ai-product-context-memory" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.title="SmoothAiProductContextMemory Viewer Pod" \
      org.opencontainers.image.description="Loopback Mímisbrunnr Console: Caddy + Python bridge + static SPA."

# Fail the build on a bridge syntax error rather than at container start.
RUN python3 -m py_compile /bridge/bridge.py && rm -rf /bridge/__pycache__

# Caddy's default storage/autosave dirs are owned by root; pre-create them for
# the caddy user so startup does not log permission-denied warnings.
RUN chmod +x /entrypoint.sh \
    && mkdir -p /config/caddy /data/caddy \
    && chown -R caddy:caddy /config /data

EXPOSE 5180
# Run as a non-root user (least privilege, consistent with the Host image).
USER caddy
ENTRYPOINT ["/entrypoint.sh"]
