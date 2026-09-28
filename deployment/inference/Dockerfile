# Lucia's local-AI worker for NVIDIA GPU servers (linux/amd64). One image per CUDA line:
#   dotnet publish src/Lucia.Inference.Worker -c Release -p:CudaLine=13 -o <context>/publish
#   docker build --platform linux/amd64 -f deployment/inference/Dockerfile -t lucia-inference:<version>-cuda13 <context>
# MCR index verified 2026-09-22: linux/amd64, linux/arm/v7, linux/arm64; ASP.NET 10.0.12.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f

# TensorSharp bundles the CUDA runtime and cuBLAS; the NVIDIA container toolkit injects the host driver.
RUN apt-get update \
    && apt-get install --no-install-recommends -y libgomp1 python3-venv \
    && python3 -m venv /opt/hf \
    && /opt/hf/bin/pip install --no-cache-dir --disable-pip-version-check \
       "https://files.pythonhosted.org/packages/py3/h/huggingface_hub/huggingface_hub-1.32.0-py3-none-any.whl#sha256=b0c7c80561969d9cdacdd55fce67ba9584cca0b9d4ea80957a3a5c1445fac5c8" \
    && /opt/hf/bin/hf --version \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o $APP_UID /models /data /cache

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    HF_HOME=/data/huggingface \
    HOME=/data \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility \
    LD_LIBRARY_PATH="/app:/app/runtimes/linux-x64/native"
WORKDIR /app
COPY publish/ ./
USER $APP_UID
VOLUME ["/models", "/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s CMD ["/usr/bin/python3", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health', timeout=4)"]
ENTRYPOINT ["dotnet", "Lucia.Inference.Worker.dll"]
