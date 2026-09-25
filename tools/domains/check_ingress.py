"""Opt-in Linux regression: real Traefik, disposable TLS, no host ports or live state.

Pass domain.yml exported by Lucia.Homelab.DomainChecks --export-ingress-fixture.
Uses only images already running in this Lucia installation, without network access.
"""
import hashlib
import json
import os
import pathlib
import ssl
import subprocess
import sys
import tempfile
import uuid

import activation_worker as worker


def run(command):
    result = subprocess.run(command, capture_output=True, text=True, timeout=90)
    if result.returncode:
        raise RuntimeError(f"Isolated ingress check failed: {command[0]}\n{result.stderr[-3000:]}")
    return result.stdout.strip()


if __name__ == "__main__":
    if sys.platform != "linux" or len(sys.argv) != 2:
        raise SystemExit("Usage: python3 check_ingress.py <exported-domain.yml> (Linux)")
    configuration = pathlib.Path(sys.argv[1]).read_bytes()
    gateway_image = run(["docker", "inspect", "--format", "{{.Image}}", "lucia-identity-gateway"])
    python_image = run(["docker", "inspect", "--format", "{{.Image}}", "lucia-homelab-host"])
    name = "lucia-domain-ingress-check-" + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix=".lucia-ingress-check-", dir=pathlib.Path.home()) as folder:
        root = pathlib.Path(folder)
        root.chmod(0o700)
        gateway, certificates = root / "gateway", root / "certificates"
        (gateway / "domains").mkdir(parents=True, mode=0o700)
        gateway.chmod(0o700)
        certificates.mkdir(mode=0o700)
        marker_text = '{"http":{"middlewares":{"fixture":{"headers":{"customResponseHeaders":{"X-Fixture":"true"}}}}}}'
        worker.write_file(gateway / "tls.yml", marker_text)
        ca, ca_key, key, csr = [root / path for path in ("root.crt", "root.key", "leaf.key", "leaf.csr")]
        extensions = root / "extensions.cnf"
        extensions.write_text("subjectAltName=DNS:lucia.lab.example.test,DNS:auth.lab.example.test,DNS:spark.lab.example.test\n"
                              "basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n")
        run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "2",
             "-subj", "/CN=Disposable Lucia ingress check", "-addext", "basicConstraints=critical,CA:TRUE",
             "-addext", "keyUsage=critical,keyCertSign,cRLSign", "-keyout", str(ca_key), "-out", str(ca)])
        run(["openssl", "req", "-new", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=lucia.lab.example.test",
             "-keyout", str(key), "-out", str(csr)])
        worker.write_file(certificates / "privkey.pem", key.read_bytes())

        def sign(path):
            run(["openssl", "x509", "-req", "-in", str(csr), "-CA", str(ca), "-CAkey", str(ca_key),
                 "-CAcreateserial", "-out", str(path), "-days", "1", "-extfile", str(extensions)])
            path.chmod(0o600)
            return hashlib.sha256(ssl.PEM_cert_to_DER_cert(path.read_text())).hexdigest()

        first = sign(certificates / "fullchain.pem")
        try:
            run(["docker", "run", "-d", "--rm", "--pull", "never", "--name", name, "--network", "none",
                 "--read-only", "--cap-drop", "ALL", "--user", f"{os.getuid()}:{os.getgid()}",
                 "--add-host", "lucia-host:127.0.0.1", "--add-host", "identity-server:127.0.0.1",
                 "--mount", f"type=bind,src={gateway},dst=/config,readonly",
                 "--mount", f"type=bind,src={certificates},dst=/domain-certificates,readonly",
                 gateway_image, "--entrypoints.host.address=:8444", "--providers.file.directory=/config",
                 "--providers.file.watch=true", "--global.checknewversion=false", "--global.sendanonymoususage=false"])
            backend = """
import http.server,json,threading,time
class Handler(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  body=json.dumps({'status':'ok','issuer':'https://auth.lab.example.test/application/o/lucia/'}).encode()
  self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(body)
 def log_message(self,*args):pass
for port in (8080,9000):
 threading.Thread(target=http.server.HTTPServer(('127.0.0.1',port),Handler).serve_forever,daemon=True).start()
while True:time.sleep(1)
"""
            run(["docker", "run", "-d", "--rm", "--pull", "never", "--name", name + "-backend",
                 "--network", "container:" + name, "--read-only", "--cap-drop", "ALL", "--entrypoint", "python3",
                 python_image, "-c", backend])

            def probe(fingerprint=None):
                code = """
import socket,ssl,hashlib,time
context=ssl.create_default_context(cafile='/root.crt')
expected=EXPECTED
for attempt in range(40):
 try:
  with socket.create_connection(('127.0.0.1',8444),timeout=2) as raw:
   with context.wrap_socket(raw,server_hostname='lucia.lab.example.test') as tls:
    assert expected is not None,'Unpublished domain unexpectedly became trusted'
    assert hashlib.sha256(tls.getpeercert(binary_form=True)).hexdigest()==expected,'Wrong certificate'
    tls.sendall(b'GET /health/live HTTP/1.1\\r\\nHost: lucia.lab.example.test\\r\\nConnection: close\\r\\n\\r\\n')
    response=b''
    while True:
     chunk=tls.recv(4096)
     if not chunk:break
     response+=chunk
    assert response.startswith(b'HTTP/1.1 200'),response[:100]
    assert b'"status": "ok"' in response
    break
 except ssl.SSLCertVerificationError:
  if expected is None:break
  if attempt==39:raise
 except (OSError,AssertionError):
  if attempt==39:raise
 time.sleep(.25)
else:raise AssertionError('Gateway check timed out')
print('verified')
""".replace("EXPECTED", repr(fingerprint))
                return run(["docker", "run", "--rm", "--pull", "never", "--network", "container:" + name,
                            "--read-only", "--cap-drop", "ALL", "--entrypoint", "python3",
                            "--mount", f"type=bind,src={ca},dst=/root.crt,readonly", python_image, "-c", code])

            worker.write_file(gateway / "domains/domain.json", configuration)
            marker = worker.refresh_ingress(root, None)
            probe()
            (gateway / "domains/domain.json").unlink()
            worker.write_file(gateway / "domains/domain.yml", configuration)
            marker = worker.refresh_ingress(root, marker)
            probe(first)
            second = sign(certificates / "renewed.pem")
            assert second != first
            updated = json.loads(configuration)
            updated["tls"]["certificates"][0]["certFile"] = "/domain-certificates/renewed.pem"
            worker.write_file(gateway / "domains/domain.yml", json.dumps(updated))
            marker = worker.refresh_ingress(root, marker)
            probe(second)
            (gateway / "domains/domain.yml").unlink()
            worker.refresh_ingress(root, marker)
            probe()
            assert (gateway / "tls.yml").read_text() == marker_text
            print("PASS: real Traefik ignores .json; .yml publication, native reload, certificate rotation and rollback all verified. No production changes.")
        except Exception:
            logs = subprocess.run(["docker", "logs", "--tail", "30", name], capture_output=True, text=True)
            print(logs.stdout + logs.stderr, flush=True)
            raise
        finally:
            subprocess.run(["docker", "rm", "-f", name + "-backend", name], capture_output=True, check=False)
