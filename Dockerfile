# syntax=docker/dockerfile:1
#
# Multi-stage build for the SmoothAiProductContextMemory Host. The published
# GHCR image is built from this file; local builds use the same path. Pairing
# is mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0-alpine — see docs/wiki/docker.md.

# ── Build stage ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /build

COPY SmoothAiProductContextMemory.slnx Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/SmoothAiProductContextMemory.Domain/SmoothAiProductContextMemory.Domain.csproj src/SmoothAiProductContextMemory.Domain/
COPY src/SmoothAiProductContextMemory.Application/SmoothAiProductContextMemory.Application.csproj src/SmoothAiProductContextMemory.Application/
COPY src/SmoothAiProductContextMemory.Infrastructure/SmoothAiProductContextMemory.Infrastructure.csproj src/SmoothAiProductContextMemory.Infrastructure/
COPY src/SmoothAiProductContextMemory.Host/SmoothAiProductContextMemory.Host.csproj src/SmoothAiProductContextMemory.Host/

RUN dotnet restore src/SmoothAiProductContextMemory.Host/SmoothAiProductContextMemory.Host.csproj

COPY src/ src/

RUN dotnet publish src/SmoothAiProductContextMemory.Host/SmoothAiProductContextMemory.Host.csproj \
      -c Release -o /app --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./

ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/generic-automation-and-it/smooth-ai-product-context-memory" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.title="SmoothAiProductContextMemory Host" \
      org.opencontainers.image.description="Context-memory HTTP API and export CLI. Database and object store are external."

# Bind 5141 to match launchSettings / HOST_AGENTS.md. The aspnet image ships a
# non-root user; run as it. Arguments pass through so `export` stays reachable.
ENV ASPNETCORE_URLS=http://+:5141 \
    ASPNETCORE_HTTP_PORTS=5141
EXPOSE 5141
USER $APP_UID

ENTRYPOINT ["./SmoothAiProductContextMemory.Host"]
