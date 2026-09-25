FROM debian:13.7-slim@sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 dnsmasq-base \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -m 0755 /opt/lucia
COPY --chmod=0644 serve.py /opt/lucia/serve.py
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
USER 65534:65534
ENTRYPOINT ["python3", "/opt/lucia/serve.py"]
