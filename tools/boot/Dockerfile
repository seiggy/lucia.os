FROM debian:13.7-slim@sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a AS prepare
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 gpgv debian-archive-keyring ca-certificates binutils kmod openssl qemu-user \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
ENTRYPOINT ["python3", "/src/tools/boot/prepare.py"]

FROM prepare AS qualification
RUN apt-get update && apt-get install -y --no-install-recommends qemu-system-x86 ovmf openssh-client \
    && rm -rf /var/lib/apt/lists/*

FROM prepare AS final
