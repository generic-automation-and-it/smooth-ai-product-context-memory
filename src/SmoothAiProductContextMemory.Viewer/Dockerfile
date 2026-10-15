# syntax=docker/dockerfile:1
#
# Viewer pod: the loopback Mímisbrunnr Console. One container runs Caddy
# (serves the static SPA and proxies /api + /bridge), the Python bridge, and
# the committed no-build SPA module. The Host image stays pure (LADR-01).
#
# Build from the repository root, not from this directory:
#   docker build -f src/SmoothAiProductContextMemory.Viewer/Dockerfile .
# The bridge imports the capture scripts as a library (LADR-09), and those live
# outside this directory in the plugin's shared script folder — unreachable from
# a Viewer-only context. The copy below is a build artifact, not a second source:
# HLD-010 holds because nothing is ever edited inside the image. Root
# .dockerignore keeps the context small and re-includes only that folder.
#
# The supervisor is the entrypoint script: it starts the bridge then execs
# Caddy, so Caddy is PID 1 and owns the bridge as a child — no extra init.

FROM caddy:2.8-alpine AS runtime

# Python 3 for the bridge. Standard library only in Phase A; P4 adds no pip
# dependency because it imports the capture client as a library (LADR-09).
RUN apk add --no-cache python3 \
    && addgroup -S caddy \
    && adduser -S caddy -G caddy

COPY src/SmoothAiProductContextMemory.Viewer/Caddyfile /etc/caddy/Caddyfile
COPY src/SmoothAiProductContextMemory.Viewer/entrypoint.sh /entrypoint.sh
COPY src/SmoothAiProductContextMemory.Viewer/wwwroot/ /srv/spa/
COPY src/SmoothAiProductContextMemory.Viewer/bridge/ /bridge/
# The capture gates the P4 bridge drives (LADR-09). Present but unimported until
# P4: the Phase-A stub stays stdlib-only and never touches the store (NFR-02).
COPY .agents/skills/_mimisbrunnr-shared/scripts/ /bridge/capture/
ENV PYTHONPATH=/bridge/capture

ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/generic-automation-and-it/smooth-ai-product-context-memory" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.title="SmoothAiProductContextMemory Viewer Pod" \
      org.opencontainers.image.description="Loopback Mímisbrunnr Console: Caddy + Python bridge + static SPA."

# Fail the build on a Python syntax error rather than at container start.
RUN python3 -m py_compile /bridge/bridge.py /bridge/capture/*.py \
    && rm -rf /bridge/__pycache__ /bridge/capture/__pycache__ \
    && chown -R caddy:caddy /bridge/capture

# Caddy's default storage/autosave dirs are owned by root; pre-create them for
# the caddy user so startup does not log permission-denied warnings.
RUN chmod +x /entrypoint.sh \
    && mkdir -p /config/caddy /data/caddy \
    && chown -R caddy:caddy /config /data

EXPOSE 5180
# Run as a non-root user (least privilege, consistent with the Host image).
USER caddy
ENTRYPOINT ["/entrypoint.sh"]
