"""Opt-in Docker/OpenSSL regression for the first-certificate startup race.

Runs only isolated, disposable containers using cached pinned images. No host
ports, Docker socket mounts, live identity state, or OS trust changes.
"""

import hashlib
import json
import os
import pathlib
import re
import ssl
import subprocess
import tempfile
import uuid

from provision import ROOT, write_file

TRAEFIK = "traefik@sha256:31267173a15b4944e797a76ffd9c419707c8d8b32fe5b610f80cd0cfa05f372d"
STEP = "smallstep/step-ca@sha256:a2b17872915c193259b75a5474c398326f41bd199f0842093e52cf4182bc8270"
PYTHON = "ghcr.io/goauthentik/server@sha256:ab9b4e8cc4ab3f8d1198d2db6aeea66bafea1963b3f2843589e0d163f97d9849"


def run(command):
    result = subprocess.run(command, capture_output=True, text=True, timeout=90)
    if result.returncode:
        raise RuntimeError(f"TLS fixture command failed ({command[0]}, exit {result.returncode}):\n{result.stderr[-4000:]}\n{result.stdout[-2000:]}")
    return result


if __name__ == "__main__":
    name = "lucia-tls-gate-check-" + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix="lucia-tls-gate-") as temporary:
        base = pathlib.Path(temporary)
        certificates, gateway, trust, scripts = [base / name for name in ("certificates", "gateway", "trust", "scripts")]
        for directory in (certificates, gateway, trust, scripts):
            directory.mkdir(mode=0o700)
        root = trust / "lucia-root-ca.crt"
        root_key = base / "root.key"
        key = base / "service.key"
        csr = base / "service.csr"
        leaf = base / "service.crt"
        extensions = base / "extensions.cnf"
        extensions.write_text("subjectAltName=DNS:identity-gateway,DNS:localhost\nbasicConstraints=critical,CA:FALSE\n"
                              "keyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth,clientAuth\n")
        run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "2",
             "-subj", "/CN=Lucia disposable TLS test", "-addext", "basicConstraints=critical,CA:TRUE",
             "-addext", "keyUsage=critical,keyCertSign,cRLSign",
             "-keyout", str(root_key), "-out", str(root)])
        run(["openssl", "req", "-new", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=identity-gateway",
             "-keyout", str(key), "-out", str(csr)])

        def sign(output):
            run(["openssl", "x509", "-req", "-in", str(csr), "-CA", str(root), "-CAkey", str(root_key),
                 "-CAcreateserial", "-out", str(output), "-days", "1", "-extfile", str(extensions)])

        sign(leaf)
        config = {
            "tcp": {"routers": {"ldap": {"entryPoints": ["ldaps"], "rule": "HostSNI(`*`)", "service": "ldap", "tls": {}}},
                    "services": {"ldap": {"loadBalancer": {"servers": [{"address": "127.0.0.1:9009"}]}}}},
            "tls": {"certificates": [{"certFile": "/certificates/identity.crt", "keyFile": "/certificates/identity.key"}],
                    "stores": {"default": {"defaultCertificate": {"certFile": "/certificates/identity.crt", "keyFile": "/certificates/identity.key"}}}},
        }
        (gateway / "tls.yml").write_text(json.dumps(config))
        (certificates / "identity.crt").write_text("incomplete certificate output")
        (certificates / "identity.key").write_text("incomplete key output")
        (scripts / "renew-certificate.sh").write_bytes((ROOT / "deployment/identity/renew-certificate.sh").read_text().encode())
        apphost = (ROOT / "src/Lucia.Homelab.Identity.AppHost/AppHost.cs").read_text()
        args = apphost.split('builder.AddContainer("identity-gateway"', 1)[1].split('.WithArgs("-c",', 1)[1].split(".WithBindMount", 1)[0]
        command = "".join(json.loads(value) for value in re.findall(r'"(?:[^"\\]|\\.)*"', args))
        assert "sha256sum -c ready.sha256" in command and "exec traefik " in command
        command += " --global.checknewversion=false --global.sendanonymoususage=false"
        try:
            run(["docker", "run", "-d", "--rm", "--pull", "never", "--name", name, "--network", "none",
                 "--read-only", "--cap-drop", "ALL", "--user", f"{os.getuid()}:{os.getgid()}", "--entrypoint", "/bin/sh",
                 "--mount", f"type=bind,src={certificates},dst=/certificates,readonly",
                 "--mount", f"type=bind,src={gateway},dst=/config,readonly", TRAEFIK, "-c", command])
            run(["docker", "run", "-d", "--rm", "--pull", "never", "--name", name + "-backend",
                 "--network", "container:" + name, "--read-only", "--cap-drop", "ALL", "--entrypoint", "python3", PYTHON, "-c",
                 "import socket,threading\n"
                 "def drain(c):\n"
                 " with c:\n"
                 "  while c.recv(4096): pass\n"
                 "s=socket.socket(); s.bind(('127.0.0.1',9009)); s.listen()\n"
                 "while True:\n"
                 " c,_=s.accept(); threading.Thread(target=drain,args=(c,),daemon=True).start()\n"])

            def client(code):
                return run(["docker", "run", "--rm", "--pull", "never", "--network", "container:" + name,
                            "--entrypoint", "python3", "--mount", f"type=bind,src={root},dst=/root.crt,readonly", PYTHON, "-c", code])

            client("import socket; s=socket.socket(); s.settimeout(2); assert s.connect_ex(('127.0.0.1',8636)) != 0; s.close()")
            write_file(certificates / "identity.key", key.read_bytes())
            write_file(certificates / "identity.crt", leaf.read_bytes())
            ready = "".join(f"{hashlib.sha256((certificates / file).read_bytes()).hexdigest()}  {file}\n"
                            for file in ("identity.crt", "identity.key"))
            write_file(certificates / "ready.sha256", ready)
            connect = (
                "import hashlib,socket,ssl,time\n"
                "ctx=ssl.create_default_context(cafile='/root.crt')\n"
                "for attempt in range(30):\n"
                " try:\n"
                "  with socket.create_connection(('127.0.0.1',8636),timeout=2) as s:\n"
                "   with ctx.wrap_socket(s,server_hostname='identity-gateway') as tls:\n"
                "    print(hashlib.sha256(tls.getpeercert(binary_form=True)).hexdigest()); break\n"
                " except (OSError,AssertionError):\n"
                "  if attempt == 29: raise\n"
                "  time.sleep(0.25)\n"
            )
            before = client(connect).stdout.strip()
            assert before == hashlib.sha256(ssl.PEM_cert_to_DER_cert(leaf.read_text())).hexdigest()
            sign(certificates / "renewing.crt")
            run(["docker", "run", "--rm", "--pull", "never", "--network", "none", "--user", f"{os.getuid()}:{os.getgid()}", "--entrypoint", "/bin/sh",
                 "--mount", f"type=bind,src={certificates},dst=/certificates",
                 "--mount", f"type=bind,src={trust},dst=/trust,readonly",
                 "--mount", f"type=bind,src={gateway},dst=/gateway",
                 "--mount", f"type=bind,src={scripts},dst=/config,readonly",
                 STEP, "/config/renew-certificate.sh", "--publish"])
            expected = hashlib.sha256(ssl.PEM_cert_to_DER_cert((certificates / "identity.crt").read_text())).hexdigest()
            assert expected != before
            client(connect.replace("print(hashlib", f"assert hashlib.sha256(tls.getpeercert(binary_form=True)).hexdigest() == '{expected}'; print(hashlib"))
            info = json.loads(run(["docker", "inspect", name]).stdout)[0]
            logs = run(["docker", "logs", name])
            assert info["RestartCount"] == 0 and info["State"]["Running"]
            assert "panic:" not in logs.stderr + logs.stdout and "Unable to parse certificate" not in logs.stdout
            assert (certificates / "identity.key").read_bytes() == key.read_bytes()
            print("TLS gateway checks passed: no early listener, verified first handshake, atomic renewal/reload, unchanged key, zero gateway crashes.")
        finally:
            subprocess.run(["docker", "rm", "-f", name + "-backend", name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
