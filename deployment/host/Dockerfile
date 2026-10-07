# MCR index verified 2026-09-22: linux/amd64, linux/arm/v7, linux/arm64; ASP.NET 10.0.12.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f

# TensorSharp supplies CUDA user-mode libraries; Docker CDI injects the host driver.
RUN apt-get update \
    && apt-get install --no-install-recommends -y libgomp1 python3-venv \
    && python3 -m venv /opt/hf \
    && /opt/hf/bin/pip install --no-cache-dir --disable-pip-version-check \
       "https://files.pythonhosted.org/packages/py3/h/huggingface_hub/huggingface_hub-1.32.0-py3-none-any.whl#sha256=b0c7c80561969d9cdacdd55fce67ba9584cca0b9d4ea80957a3a5c1445fac5c8" \
    && /opt/hf/bin/hf --version \
    && python3 -m venv /opt/certbot \
    && /opt/certbot/bin/pip install --no-cache-dir --disable-pip-version-check "certbot==5.8.0" "certbot-dns-cloudflare==5.8.0" \
    && /opt/certbot/bin/certbot --version \
    && rm -rf /var/lib/apt/lists/*

# Copies AdGuard settings from the primary instance to the others; Lucia runs it once per sync.
COPY --from=ghcr.io/bakito/adguardhome-sync:v0.9.3@sha256:b3499852640866c5742fd72cca190f40e2741c279d60fd62b9130cd8142aafec /opt/go/adguardhome-sync /opt/adguardhome-sync/adguardhome-sync

ENV PATH="/opt/hf/bin:${PATH}" \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    HF_HOME=/data/huggingface \
    HOME=/data \
    LD_LIBRARY_PATH="/app:/app/runtimes/linux-arm64/native"
WORKDIR /app
COPY publish/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lucia.Homelab.Server.dll"]
