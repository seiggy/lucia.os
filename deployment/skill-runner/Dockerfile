# The image Lucia's Spark skill runner (tools/skills/skill_worker.py) runs skill scripts in: bash, Python 3, Node 24 and the .NET 10 SDK.
# The worker builds it on the Spark and tags it with a hash of this file, so any change here builds a new image. Base images are pinned
# by multi-architecture index digest (linux/amd64 and linux/arm64), checked against the registries on 2026-10-14; never use a tag alone.

# Node 24.21.0 on Debian bookworm (glibc 2.36, older than the .NET image's Ubuntu 24.04, so its binary runs there).
FROM node:24-bookworm-slim@sha256:d6aa754f16b3197301076f047b5def2f02ea1dbbc2ca920407d46d7ec7f87b20 AS node

# .NET SDK 10.0.403 (runtime 10.0.14) on Ubuntu 24.04.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b

RUN apt-get update \
    && apt-get install --no-install-recommends -y bash ca-certificates python3 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=node /usr/local/bin/node /usr/local/bin/node

# Containers run read-only as nobody with /tmp as the only writable place (the worker sets HOME and the caches there).
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    PYTHONDONTWRITEBYTECODE=1

RUN bash --version | head -n 1 && python3 --version && node --version && dotnet --version
WORKDIR /skill
