"""Bounded Debian 13.7 UEFI discovery builder; no disk or deployment operations."""

import argparse
import datetime as dt
import gzip
import hashlib
import io
import ipaddress
import json
import lzma
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import time
import urllib.parse
import urllib.request

BUILD = "20250803+deb13u7"
ARCHIVE = "https://deb.debian.org/debian/"
DIST = "dists/trixie/"
IMAGES = f"main/installer-amd64/{BUILD}/images/"
KEYRING = "/usr/share/keyrings/debian-archive-keyring.pgp"
BOOT = "debian-installer/amd64/"
DISCOVERY_ROUTE = "/api/boot/discovery.cfg"
PUBLIC = (
    BOOT + "bootnetx64.efi", BOOT + "grubx64.efi", BOOT + "linux",
    BOOT + "initrd.gz", BOOT + "grub/grub.cfg", BOOT + "grub/font.pf2",
    "lucia/lucia-overlay.cpio.gz", BOOT + "grub/lucia-theme.txt",
)
GRUB_PREAMBLE = [
    "set default=0", "set timeout=3", "set timeout_style=menu",
    "set menu_color_normal=light-gray/black", "set menu_color_highlight=white/blue",
    "if loadfont $prefix/font.pf2; then", "set gfxmode=1024x768,auto", "set gfxpayload=keep",
    "insmod efi_gop", "insmod gfxterm", "insmod gfxmenu", "terminal_output gfxterm",
    "set theme=$prefix/lucia-theme.txt", "fi",
]
READ_ONLY_TITLE = b"register only, installation is off"
INSTALL_TITLE = b"register, then install when approved"
# Lucia colors for every text console. fb=false keeps d-i on the kernel console
# (not bterm, whose VGA palette is fixed), so newt's blue/gray/red roles become
# navy page, pale cards and accent blue. quiet keeps kernel chatter off screen.
CONSOLE_ARGS = (
    "quiet fb=false vt.default_red=32,40,25,212,18,121,151,243,88,91,128,242,151,198,185,255 "
    "vt.default_grn=40,91,113,222,23,81,179,245,103,131,207,204,179,162,205,255 "
    "vt.default_blu=57,221,78,250,34,168,255,249,125,234,172,140,255,237,252,255"
)
PINS = {
    "SHA256SUMS": "31a1a4fc99c5b0a729d261f137cd7e1599bfa6f214f83fe4e596543d8ccf2dec",
    "netboot/netboot.tar.gz": "82b46512931807ac314aaf0d9921163a806a2bd8a584d9c88ad17814a5691c5f",
    "netboot/" + BOOT + "initrd.gz": "57303d157cffce3fa402301667fbc9b2280aa7372082b081e777375ae49da5f2",
}
LIB_PACKAGES = ("libc6", "libgcc-s1", "libstdc++6", "libssl3t64", "zlib1g", "libzstd1")
FIRMWARE_INDEX = "non-free-firmware/binary-amd64/Packages.xz"
LIMIT = 256 * 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def run(args, **kwargs):
    result = subprocess.run(args, check=True, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=120, **kwargs)
    return result.stdout


def safe_path(name):
    require(isinstance(name, str) and 0 < len(name) < 512, "Invalid archive path")
    require("\\" not in name and "\0" not in name, "Invalid archive path")
    while name.startswith("./"):
        name = name[2:]
    name = name.rstrip("/")
    require(name and not name.startswith("/") and
            all(p not in ("", ".", "..") for p in name.split("/")),
            "Unsafe archive path")
    return name


def no_symlinks(path):
    path = Path(path).absolute()
    for part in (path, *path.parents):
        require(not part.is_symlink(), f"Symlinked local path: {part}")
        if sys.platform == "linux" and part.exists():
            info = part.stat()
            require(info.st_uid in (0, os.geteuid()) and not info.st_mode & 0o022,
                    f"Untrusted local path owner/permissions: {part}")
    return path


def bounded_file(path, limit):
    path = no_symlinks(path)
    info = path.stat()
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and
            info.st_size <= limit, f"Not a bounded, single-link regular file: {path}")
    data = path.read_bytes()
    require(len(data) <= limit, "File grew beyond limit")
    return data


def write(path, data, mode=0o644):
    path.parent.mkdir(parents=True, exist_ok=True)
    no_symlinks(path)
    with path.open("xb") as stream:
        stream.write(data)
    path.chmod(mode)


class Downloads:
    def __init__(self, directory, replay=None):
        self.directory = directory
        self.replay = no_symlinks(replay) if replay else None
        self.records = {}

    def get(self, relative, maximum, expected=None, size=None):
        relative = safe_path(relative)
        require(maximum <= LIMIT and (size is None or size <= maximum), "Download too large")
        url = ARCHIVE + urllib.parse.quote(relative, safe="/+")
        name = sha(url.encode()) + ".bin"
        if self.replay:
            data = bounded_file(self.replay / name, maximum)
        else:
            request = urllib.request.Request(url, headers={"User-Agent": "Lucia-readonly-builder/1"})
            started = time.monotonic()
            with urllib.request.build_opener(NoRedirect).open(request, timeout=30) as response:
                require(response.url == url, "Unexpected Debian download redirect")
                require(int(response.headers.get("Content-Length", "0")) <= maximum,
                        "Download exceeds size bound")
                chunks, total = [], 0
                while True:
                    chunk = response.read(min(1024 * 1024, maximum + 1 - total))
                    if not chunk:
                        break
                    chunks.append(chunk)
                    total += len(chunk)
                    require(total <= maximum and time.monotonic() - started < 300,
                            "Download size/time bound exceeded")
                data = b"".join(chunks)
        digest = sha(data)
        require(expected is None or digest == expected, f"SHA256 mismatch: {relative}")
        require(size is None or len(data) == size, f"Size mismatch: {relative}")
        write(self.directory / name, data)
        self.records[relative] = {"url": url, "file": name, "size": len(data), "sha256": digest}
        return data


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        raise ValueError("Debian download redirects are not allowed")


def release_fields(text):
    result, key = {}, None
    for line in text.splitlines():
        if line.startswith(" ") and key:
            result[key] += "\n" + line[1:]
        elif ":" in line:
            key, value = line.split(":", 1)
            require(key not in result, "Duplicate metadata field")
            result[key] = value.strip()
    return result


def verify_release(inrelease, keyring, evidence):
    source = evidence / "InRelease"
    write(source, inrelease)
    destination = evidence / "Release"
    status = run(["gpgv", "--keyring", str(no_symlinks(keyring)), "--status-fd", "1",
                  "--output", str(destination), str(source)])
    fingerprints = re.findall(rb"^\[GNUPG:\] VALIDSIG ([A-F0-9]{40,64}) ", status, re.M)
    require(fingerprints, "No valid Debian archive signature")
    fields = release_fields(destination.read_text())
    require(fields.get("Origin") == "Debian" and fields.get("Codename") == "trixie" and
            fields.get("Version") == "13.7", "Not the pinned Debian 13.7 release")
    from email.utils import parsedate_to_datetime
    now = dt.datetime.now(dt.timezone.utc)
    released = parsedate_to_datetime(fields["Date"])
    require(released <= now + dt.timedelta(days=1) and now - released < dt.timedelta(days=120),
            "Stale or future-dated Release; select and requalify a new installer explicitly")
    if "Valid-Until" in fields:
        require(now < parsedate_to_datetime(fields["Valid-Until"]), "Expired Release")
    entries = {}
    for line in fields["SHA256"].splitlines():
        if not line.strip():
            continue
        digest, size, name = line.split()
        name = safe_path(name)
        require(name not in entries and re.fullmatch("[a-f0-9]{64}", digest), "Invalid Release hash")
        entries[name] = (digest, int(size))
    write(evidence / "gpgv-status.txt", status)
    return fields, entries, [item.decode() for item in fingerprints]


def manifest_entries(data):
    result = {}
    for line in data.decode().splitlines():
        digest, name = line.split(maxsplit=1)
        name = safe_path(name.lstrip("*"))
        require(name not in result and re.fullmatch("[a-f0-9]{64}", digest), "Invalid image manifest")
        result[name] = digest
    return result


def package_index(data):
    with lzma.open(io.BytesIO(data)) as stream:
        unpacked = stream.read(LIMIT + 1)
    require(len(unpacked) <= LIMIT, "Package index expands beyond bound")
    result = {}
    for paragraph in unpacked.decode().split("\n\n"):
        fields = release_fields(paragraph)
        if fields.get("Architecture") in ("amd64", "all") and "Package" in fields:
            name = fields["Package"]
            if name in result:
                require(fields["Version"] != result[name]["Version"], "Duplicate package version")
                newer = subprocess.run(["dpkg", "--compare-versions", fields["Version"],
                                        "gt", result[name]["Version"]], timeout=10)
                require(newer.returncode in (0, 1), "Invalid Debian package version")
                if newer.returncode != 0:
                    continue
            result[name] = fields
    return result


def tar_members(data):
    """Read regular files/directories/symlinks only, without extracting anything."""
    result, total = {}, 0
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:*") as archive:
        for member in archive:
            if member.name in (".", "./"):
                continue
            name = safe_path(member.name)
            require(name not in result and len(result) < 30000, "Duplicate/oversized tar archive")
            require(member.isfile() or member.isdir() or member.issym(), "Unsupported tar entry")
            require(0 <= member.size <= LIMIT and total + member.size <= LIMIT, "Tar exceeds bound")
            total += member.size
            content = archive.extractfile(member).read() if member.isfile() else member.linkname.encode()
            mode = (stat.S_IFREG if member.isfile() else
                    stat.S_IFDIR if member.isdir() else stat.S_IFLNK) | (member.mode & 0o777)
            result[name] = (mode, content)
    return result


def deb_members(data):
    require(data.startswith(b"!<arch>\n"), "Invalid Debian ar archive")
    offset, payload = 8, None
    while offset < len(data):
        header = data[offset:offset + 60]
        require(len(header) == 60 and header[-2:] == b"`\n", "Invalid ar header")
        size = int(header[48:58])
        name = header[:16].decode().strip().rstrip("/")
        offset += 60
        require(0 <= size <= LIMIT and offset + size <= len(data), "Invalid ar member size")
        if name in ("data.tar.xz", "data.tar.gz", "data.tar"):
            require(payload is None, "Duplicate Debian payload")
            payload = data[offset:offset + size]
        offset += size + (size % 2)
    require(payload is not None, "Unsupported Debian payload compression")
    return tar_members(payload)


def cpio_members(data):
    with gzip.GzipFile(fileobj=io.BytesIO(data)) as stream:
        raw = stream.read(LIMIT + 1)
    require(len(raw) <= LIMIT, "Initrd expands beyond bound")
    result, offset = {}, 0
    while offset + 110 <= len(raw):
        header = raw[offset:offset + 110]
        require(header[:6] == b"070701", "Unsupported initrd cpio format")
        fields = [int(header[i:i + 8], 16) for i in range(6, 110, 8)]
        mode, size, namesize = fields[1], fields[6], fields[11]
        require(0 < namesize < 512 and size <= LIMIT, "Invalid cpio entry")
        offset += 110
        require(offset + namesize <= len(raw) and raw[offset + namesize - 1] == 0, "Invalid cpio name")
        name = raw[offset:offset + namesize - 1].decode()
        offset = (offset + namesize + 3) & ~3
        require(offset + size <= len(raw), "Truncated cpio payload")
        content = raw[offset:offset + size]
        offset = (offset + size + 3) & ~3
        if name == "TRAILER!!!":
            require(not raw[offset:].strip(b"\0"), "Unexpected additional stock cpio data")
            return result
        if name in (".", "./"):
            continue
        name = safe_path(name)
        require(name not in result and len(result) < 30000, "Duplicate/oversized cpio archive")
        result[name] = (mode, content)
    raise ValueError("Missing cpio trailer")


def cpio_gz(files):
    expanded = dict(files)
    for name in list(files):
        safe_path(name)
        for parent in PurePosixPath(name).parents:
            if str(parent) != ".":
                existing = expanded.setdefault(str(parent), (stat.S_IFDIR | 0o755, b""))
                require(stat.S_ISDIR(existing[0]), "Non-directory archive parent")
    raw = io.BytesIO()
    for inode, (name, (mode, content)) in enumerate(
            [*sorted(expanded.items()), ("TRAILER!!!", (0, b""))], 1):
        encoded = name.encode() + b"\0"
        fields = (inode, mode, 0, 0, 1, 0, len(content), 0, 0, 0, 0, len(encoded), 0)
        raw.write(b"070701" + b"".join(f"{value:08x}".encode() for value in fields))
        raw.write(encoded)
        raw.write(b"\0" * (-raw.tell() % 4))
        raw.write(content)
        raw.write(b"\0" * (-raw.tell() % 4))
    raw.write(b"\0" * (-raw.tell() % 512))
    return gzip.compress(raw.getvalue(), compresslevel=9, mtime=0)


def put(files, name, content, executable=False):
    require(name not in files, f"Duplicate overlay path: {name}")
    files[safe_path(name)] = (stat.S_IFREG | (0o755 if executable else 0o644), content)


def package(downloads, index, name, records):
    require(name in index, f"Required matching Debian package unavailable: {name}")
    entry = index[name]
    data = downloads.get(entry["Filename"], 64 * 1024 * 1024,
                         entry["SHA256"], int(entry["Size"]))
    records[name] = {key: entry[key] for key in ("Version", "Architecture", "Filename", "SHA256", "Size")}
    return deb_members(data)


def public_ca(path):
    path = no_symlinks(path)
    data = bounded_file(path, 32768)
    require(data.count(b"-----BEGIN CERTIFICATE-----") == 1 and
            data.count(b"-----END CERTIFICATE-----") == 1 and
            not re.search(rb"PRIVATE|SECRET", data), "Supply exactly one public PEM CA, never a key")
    certificate = re.fullmatch(rb"\s*-----BEGIN CERTIFICATE-----[\r\nA-Za-z0-9+/=]+"
                               rb"-----END CERTIFICATE-----\s*", data)
    require(certificate, "Only a PEM certificate is accepted")
    text = run(["openssl", "x509", "-noout", "-text", "-checkend", "0"], input=data)
    require(b"CA:TRUE" in text, "Certificate is not a CA")
    run(["openssl", "verify", "-CAfile", str(path), str(path)])
    return data


def server_url(value):
    parsed = urllib.parse.urlsplit(value)
    require(parsed.scheme == "https" and parsed.hostname and parsed.username is None and
            parsed.password is None and parsed.path in ("", "/") and not parsed.query and
            not parsed.fragment and re.fullmatch(r"https://[A-Za-z0-9.-]+(?::[0-9]{1,5})?/?", value),
            "Controller must be a plain HTTPS hostname origin")
    require(parsed.port is None or 0 < parsed.port < 65536, "Invalid server port")
    return value.rstrip("/")


def validate_grub(data, server):
    lines = [line.strip() for line in data.decode().splitlines() if line.strip()]
    expected_tail = [
        f"linux /{BOOT}linux auto=true priority=critical netcfg/choose_interface=auto "
        f"url={server_url(server)}{DISCOVERY_ROUTE} {CONSOLE_ARGS} ---",
        f"initrd /{BOOT}initrd.gz /lucia/lucia-overlay.cpio.gz",
        "}",
    ]
    n = len(GRUB_PREAMBLE)
    require(len(lines) == n + 4 and lines[:n] == GRUB_PREAMBLE and
            re.fullmatch(r"menuentry '[^'\r\n$\\]+' \{", lines[n]) and lines[n + 1:] == expected_tail,
            "GRUB must offer only guarded Lucia discovery at the controller API route")


def agent_files(directory):
    directory = no_symlinks(directory)
    require(directory.is_dir(), "Missing trusted maintainer agent publish")
    result, total = {}, 0
    names = list(directory.iterdir())
    require(0 < len(names) <= 512, "Agent publish file-count bound exceeded")
    for path in names:
        require(re.fullmatch(r"[A-Za-z0-9_.+-]+", path.name), "Unexpected publish filename")
        require(path.name in ("lucia-node-agent", "createdump",
                              "lucia-node-agent.deps.json", "lucia-node-agent.runtimeconfig.json") or
                path.suffix in (".so", ".dll", ".pdb"), "Non-publish/credential file rejected")
        data = bounded_file(path, 32 * 1024 * 1024)
        total += len(data)
        require(total <= 128 * 1024 * 1024, "Agent publish exceeds bound")
        require(not re.search(rb"-----BEGIN [A-Z ]*PRIVATE KEY-----", data), "Private key rejected")
        put(result, "usr/lib/lucia/agent/" + path.name, data,
            path.name in ("lucia-node-agent", "createdump"))
    required = ("lucia-node-agent", "lucia-node-agent.dll", "lucia-node-agent.deps.json",
                "lucia-node-agent.runtimeconfig.json", "libcoreclr.so", "libhostfxr.so")
    require(all("usr/lib/lucia/agent/" + name in result for name in required), "Incomplete agent publish")
    app = result["usr/lib/lucia/agent/lucia-node-agent"][1]
    require(app[:5] == b"\x7fELF\x02" and app[18:20] == b"\x3e\x00", "Agent is not Linux x86-64 ELF")
    return result


def library_overlay(downloads, index, records):
    files = {}
    for name in LIB_PACKAGES:
        members = package(downloads, index, name, records)
        for path, (mode, data) in members.items():
            if not path.startswith("usr/lib/x86_64-linux-gnu/") or stat.S_ISDIR(mode):
                continue
            basename = PurePosixPath(path).name
            if ".so" not in basename or "/ossl-modules/" in path:
                continue
            target = "usr/lib/lucia/agent/" + basename
            require(target not in files, "Duplicate runtime library")
            if stat.S_ISLNK(mode):
                link = data.decode()
                require("/" not in link and link not in (".", ".."), "Unsafe library symlink")
            files[target] = (mode, data)
    for name, (mode, content) in files.items():
        if stat.S_ISLNK(mode):
            require("usr/lib/lucia/agent/" + content.decode() in files, "Broken runtime symlink")
    require("usr/lib/lucia/agent/ld-linux-x86-64.so.2" in files, "Missing ELF loader")
    return files


def network_firmware_overlay(downloads, index, records):
    files = {}
    for path, (mode, data) in package(downloads, index, "firmware-realtek", records).items():
        if path.startswith("lib/firmware/"):
            path = "usr/" + path
        if path.startswith("usr/lib/firmware/rtl_nic/") and not stat.S_ISDIR(mode):
            require(stat.S_ISREG(mode) and path.endswith(".fw"), "Unexpected Realtek Ethernet firmware entry")
            put(files, path, data)
        elif path == "usr/share/doc/firmware-realtek/copyright":
            require(stat.S_ISREG(mode), "Missing firmware redistribution terms")
            put(files, path, data)
    require("usr/lib/firmware/rtl_nic/rtl8125b-2.fw" in files, "Missing RTL8125B Ethernet firmware")
    require("usr/share/doc/firmware-realtek/copyright" in files, "Missing firmware redistribution terms")
    return files


def storage_overlay(downloads, index, stock, work, records):
    kernels = {path.split("/")[3] for path in stock if path.startswith("usr/lib/modules/")}
    require(len(kernels) == 1, "Cannot determine a single installer kernel ABI")
    kernel = kernels.pop()
    installed = {}
    for paragraph in stock["var/lib/dpkg/status"][1].decode().split("\n\n"):
        fields = release_fields(paragraph)
        if "Package" in fields:
            installed[fields["Package"]] = fields
    requested = [f"{stem}-{kernel}-di" for stem in
                 ("sata-modules", "scsi-modules")]
    done, result = set(), {}
    while requested:
        name = requested.pop()
        if name in done or name in installed:
            continue
        done.add(name)
        require(len(done) <= 32 and name.endswith(f"-{kernel}-di"),
                "Storage closure escaped matching kernel udebs")
        require(name in index, f"Unavailable kernel udeb: {name}")
        fields = index[name]
        for dep in fields.get("Depends", "").split(","):
            dep = dep.strip()
            if dep:
                require("|" not in dep, "Ambiguous kernel dependency")
                requested.append(dep.split()[0])
        for path, (mode, content) in package(downloads, index, name, records).items():
            if path.startswith("lib/modules/"):
                path = "usr/" + path
            if path.startswith(f"usr/lib/modules/{kernel}/") and stat.S_ISREG(mode):
                if path in stock:
                    require(content == stock[path][1], "Kernel module differs from stock initrd")
                else:
                    require(path not in result, "Duplicate module")
                    result[path] = (mode, content)
    modules = {name: value for name, value in stock.items()
               if name.startswith(f"usr/lib/modules/{kernel}/") and stat.S_ISREG(value[0])}
    modules.update(result)
    for name, (mode, data) in modules.items():
        write(work / name.removeprefix("usr/"), data, mode & 0o777)
    run(["depmod", "-b", str(work), kernel])
    directory = work / "lib" / "modules" / kernel
    for path in directory.glob("modules.*"):
        result[f"usr/lib/modules/{kernel}/{path.name}"] = (stat.S_IFREG | 0o644, path.read_bytes())
    for module in ("nvme", "ahci", "ata_piix", "virtio_pci", "virtio_blk", "virtio_scsi", "sd_mod"):
        output = run(["modprobe", "-d", str(work), "-S", kernel, "--show-depends", module])
        require(b"insmod " in output or b"builtin " in output, f"Missing module closure: {module}")
    return kernel, result


def runtime_check(files, work):
    root = work / "runtime-check"
    for name, (mode, data) in files.items():
        if not name.startswith("usr/lib/lucia/"):
            continue
        path = root / name
        if stat.S_ISREG(mode):
            write(path, data, mode & 0o777)
        elif stat.S_ISLNK(mode):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.symlink_to(data.decode())
    agent = root / "usr/lib/lucia/agent"
    lib = agent
    needed = set()
    optional = {"libcoreclrtraceptprovider.so"}
    for path in agent.iterdir():
        if path.is_symlink() or path.name in optional or path.read_bytes()[:4] != b"\x7fELF":
            continue
        headers = run(["readelf", "-h", str(path)])
        require(b"Advanced Micro Devices X86-64" in headers, "Non-amd64 runtime library")
        dynamic = run(["readelf", "-d", str(path)])
        needed.update(item.decode() for item in re.findall(rb"Shared library: \[([^\]]+)\]", dynamic))
    require(all((lib / name).exists() or (agent / name).exists() for name in needed),
            "ELF dependency closure incomplete: " +
            ", ".join(sorted(name for name in needed if not (lib / name).exists() and not (agent / name).exists())))
    for name in ("libssl.so.3", "libcrypto.so.3", "libz.so.1"):
        require((lib / name).exists(), f"Missing dynamically loaded dependency: {name}")
    command = [str(lib / "ld-linux-x86-64.so.2"), "--inhibit-cache",
               "--library-path", str(lib), str(agent / "lucia-node-agent"), "--help"]
    native = platform.machine() in ("x86_64", "AMD64")
    if not native:
        command.insert(0, "qemu-x86_64")
    environment = dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1",
                       DOTNET_EnableDiagnostics="0")
    output = run(command, env=environment)
    require(b"lucia-node-agent inspect" in output and b"lucia-node-agent discover" in output,
            "Agent CLI smoke test failed")
    return {"elfNeeded": sorted(needed), "cliExecuted": True, "inspectExecuted": False,
            "execution": "native" if native else "qemu-user",
            "optionalNotRequired": ["LTTng", "Kerberos", "ICU"],
            "disclaimer": "Userspace check only; not UEFI boot or target hardware qualification"}


def prepare_bundle(*, output: str | Path, node_agent: str | Path, ca_file: str | Path,
                   server: str = "https://spark-9423", connect_address: str | None = None,
                   keyring: str | Path = KEYRING, replay: str | Path | None = None,
                   boot_assets: str | Path | None = None,
                   ca_der_sha256: str | None = None, installation: bool = False) -> dict:
    """Create a new owned bundle and return its verified schemaVersion=1 receipt.

    Inputs are local trusted-maintainer files. This performs no deployment,
    service publication, admission-window change, or target installation.
    """
    require(sys.platform == "linux", "Build in the isolated Linux builder, not on a deployment host")
    require(type(installation) is bool, "Installation profile must be explicit")
    server = server_url(server)
    connect = str(ipaddress.ip_address(connect_address)) if connect_address else ""
    if connect:
        require(ipaddress.ip_address(connect) in ipaddress.ip_network("192.168.0.0/23"),
                "Connect override must be on the approved test LAN")
    ca = public_ca(Path(ca_file))
    ca_der_hash = sha(run(["openssl", "x509", "-outform", "DER"], input=ca))
    if ca_der_sha256 is not None:
        require(re.fullmatch("[a-f0-9]{64}", ca_der_sha256) and ca_der_hash == ca_der_sha256,
                "Public CA DER SHA256 does not match the supplied trust pin")
    files = agent_files(node_agent)
    source_hashes = {name.removeprefix("usr/lib/lucia/agent/"): sha(data)
                     for name, (_, data) in files.items()}
    output = no_symlinks(output)
    require(not output.exists(), "Output already exists; use verify or a new owned directory")
    output.mkdir(mode=0o700)
    write(output / ".lucia-build", b"readonly-discovery-v1\n", 0o600)
    evidence, work = output / "evidence", output / "work"
    downloads = Downloads(evidence / "downloads", replay)
    inrelease = downloads.get(DIST + "InRelease", 512 * 1024)
    fields, release, fingerprints = verify_release(inrelease, keyring, evidence)
    digest, size = release[IMAGES + "SHA256SUMS"]
    sums = downloads.get(DIST + IMAGES + "SHA256SUMS", 1024 * 1024, digest, size)
    require(sha(sums) == PINS["SHA256SUMS"], "Pinned installer manifest changed")
    manifest = manifest_entries(sums)
    for name, digest in PINS.items():
        if name != "SHA256SUMS":
            require(manifest[name] == digest, "Pinned installer digest changed")
    netboot = downloads.get(DIST + IMAGES + "netboot/netboot.tar.gz", 128 * 1024 * 1024,
                           manifest["netboot/netboot.tar.gz"])
    members = tar_members(netboot)
    stock_files = {}
    for name in PUBLIC[:6]:
        mode, data = members[name]
        require(stat.S_ISREG(mode), f"Not a regular official boot artifact: {name}")
        key = "netboot/" + name
        if key in manifest:
            require(sha(data) == manifest[key], f"Individual upstream artifact mismatch: {name}")
        stock_files[name] = data
    require(sha(stock_files[BOOT + "initrd.gz"]) == PINS["netboot/" + BOOT + "initrd.gz"],
            "Stock initrd mismatch")
    stock = cpio_members(stock_files[BOOT + "initrd.gz"])
    require("usr/bin/wget" in stock or "bin/wget" in stock, "Missing stock HTTPS wget")
    indexes = []
    for name in ("main/binary-amd64/Packages.xz", "main/debian-installer/binary-amd64/Packages.xz", FIRMWARE_INDEX):
        digest, size = release[name]
        indexes.append(package_index(downloads.get(DIST + name, 32 * 1024 * 1024, digest, size)))
    records = {}
    libraries = library_overlay(downloads, indexes[0], records)
    require(not files.keys() & libraries.keys(), "Publish collides with private Debian libraries")
    files.update(libraries)
    firmware = network_firmware_overlay(downloads, indexes[2], records)
    require(not files.keys() & firmware.keys(), "Firmware collides with the discovery overlay")
    files.update(firmware)
    kernel, modules = storage_overlay(downloads, indexes[1], stock, work, records)
    files.update(modules)
    runtime = runtime_check(files, work)
    deployment = (Path(boot_assets) if boot_assets is not None else
                  Path(__file__).resolve().parents[2] / "deployment" / "boot")
    for src, dest in (("discover-and-wait", "usr/lib/lucia/discover-and-wait"),
                      ("screen.sh", "usr/lib/lucia/screen.sh"),
                      ("partitioner-guard", "usr/lib/partman/init.d/00lucia-approval")):
        data = bounded_file(deployment / src, 16384).replace(b"\r\n", b"\n")
        run(["sh", "-n"], input=data)
        put(files, dest, data, True)
    if installation:
        put(files, "etc/lucia/installation-enabled", b"approved-installation-v1\n")
        put(files, "usr/lib/lucia/installation-guard", files["usr/lib/partman/init.d/00lucia-approval"][1], True)
        finish = bounded_file(deployment / "finish-install", 16384).replace(b"\r\n", b"\n")
        run(["sh", "-n"], input=finish)
        put(files, "usr/lib/lucia/finish-install", finish, True)
    put(files, "etc/lucia/public-ca.crt", ca)
    config = (f"LUCIA_SERVER='{server}'\nLUCIA_CONNECT_ADDRESS='{connect}'\n"
              f"LUCIA_KERNEL_ABI='{kernel}'\n").encode()
    put(files, "etc/lucia/discovery.conf", config)
    wgetrc = stock.get("etc/wgetrc", (0, b""))[1]
    require(not re.search(rb"(?m)^\s*(check_certificate|ca_certificate)\s*=", wgetrc),
            "Review stock wget TLS overrides before adapting")
    wget_ca = "/etc/lucia/public-ca.crt"
    if installation:
        trusted = package(downloads, indexes[0], "ca-certificates", records)
        roots = [data for name, (mode, data) in sorted(trusted.items())
                 if name.startswith("usr/share/ca-certificates/") and name.endswith(".crt") and stat.S_ISREG(mode)]
        require(roots and all(b"PRIVATE KEY" not in value and b"BEGIN CERTIFICATE" in value for value in roots),
                "The signed Debian CA bundle is incomplete")
        put(files, "etc/lucia/installer-ca-bundle.crt", b"\n".join([*roots, ca]))
        wget_ca = "/etc/lucia/installer-ca-bundle.crt"
    put(files, "etc/wgetrc", wgetrc + ("\nca_certificate = " + wget_ca + "\n").encode())
    for name in files:
        for parent in PurePosixPath(name).parents:
            if str(parent) in stock:
                require(stat.S_ISDIR(stock[str(parent)][0]), "Overlay parent is a stock symlink/file")
    overlay = cpio_gz(files)
    require(len(overlay) <= 128 * 1024 * 1024, "Overlay exceeds bound")
    grub = bounded_file(deployment / "grub.cfg.in", 16384).replace(b"\r\n", b"\n")
    grub = grub.replace(b"@DISCOVERY_URL@", (server + DISCOVERY_ROUTE).encode())
    if installation:
        grub = grub.replace(READ_ONLY_TITLE, INSTALL_TITLE)
    validate_grub(grub, server)
    theme = bounded_file(deployment / "grub-theme.txt", 16384).replace(b"\r\n", b"\n")
    theme.decode("utf-8")
    for name, data in stock_files.items():
        write(output / "public" / name, grub if name.endswith("/grub.cfg") else data)
    write(output / "public" / BOOT / "grub/lucia-theme.txt", theme)
    write(output / "public/lucia/lucia-overlay.cpio.gz", overlay)
    receipt = {
        "schemaVersion": 1, "purpose": "approved-installation-with-managed-enrollment" if installation else "read-only-discovery-no-installation",
        "debianVersion": "13.7", "installerBuild": BUILD, "kernelAbi": kernel,
        "release": {"date": fields["Date"], "sha256": sha(inrelease),
                    "validSignatureFingerprints": fingerprints,
                    "keyringSha256": sha(bounded_file(keyring, 4 * 1024 * 1024))},
        "downloads": downloads.records, "packages": records, "agentFiles": source_hashes,
        "upstream": {name: sha(data) for name, data in stock_files.items()},
        "artifacts": {name: sha((output / "public" / name).read_bytes()) for name in PUBLIC},
        "overlayFiles": {name: {"mode": mode, "sha256": sha(data)}
                         for name, (mode, data) in sorted(files.items())},
        "controller": {"server": server, "discoveryUrl": server + DISCOVERY_ROUTE,
                       "connectAddress": connect, "publicCaSha256": sha(ca),
                       "publicCaDerSha256": ca_der_hash},
        "runtimeCheck": runtime,
        "qualification": {"uefiVmBoot": False, "diskUnchangedTest": False,
                          "realHardware": False, "secureBootTarget": "owner-disabled"},
        "readinessPrerequisites": [
            "Trusted test LAN 192.168.0.0/23; external initrd and GRUB config are NOT authenticated by Secure Boot",
            "Working DNS for the HTTPS hostname (agent IP override does not fix GNU wget preseed DNS)",
            "Accurate target clock; valid HTTPS certificate under the supplied public CA",
            "Matching network/storage controller support; unsupported controllers never authorize another disk",
            "Separate isolated UEFI boot and unchanged-disposable-disk qualification before live use",
        ],
    }
    write(output / "receipt.json", (json.dumps(receipt, indent=2, sort_keys=True) + "\n").encode())
    shutil.rmtree(work)
    return verify_bundle(output)


def verify_bundle(directory: str | Path) -> dict:
    """Return the v1 receipt after offline integrity/allowlist checks; not attestation."""
    directory = no_symlinks(directory)
    require(bounded_file(directory / ".lucia-build", 64) == b"readonly-discovery-v1\n",
            "Not a Lucia-owned output")
    receipt = json.loads(bounded_file(directory / "receipt.json", 4 * 1024 * 1024))
    require(receipt["schemaVersion"] == 1 and set(receipt["artifacts"]) == set(PUBLIC),
            "Unexpected receipt/public allowlist")
    public = directory / "public"
    actual = set()
    for path in public.rglob("*"):
        require(not path.is_symlink(), "Symlink in public bundle")
        if not path.is_dir():
            name = path.relative_to(public).as_posix()
            actual.add(name)
            require(sha(bounded_file(path, LIMIT)) == receipt["artifacts"].get(name),
                    f"Public artifact changed: {name}")
    require(actual == set(PUBLIC), "Unexpected/missing public file")
    server = server_url(receipt["controller"]["server"])
    require(receipt["controller"].get("discoveryUrl") == server + DISCOVERY_ROUTE,
            "Receipt does not name the supported discovery API route")
    validate_grub(bounded_file(public / BOOT / "grub/grub.cfg", 16384), server)
    for entry in receipt["downloads"].values():
        name = safe_path(entry["file"])
        data = bounded_file(directory / "evidence/downloads" / name, LIMIT)
        require(len(data) == entry["size"] and sha(data) == entry["sha256"], "Evidence changed")
    require(receipt["upstream"][BOOT + "initrd.gz"] == receipt["artifacts"][BOOT + "initrd.gz"] ==
            PINS["netboot/" + BOOT + "initrd.gz"], "Upstream initrd was modified")
    return receipt


verify = verify_bundle


def installation_payload_sha256(receipt):
    """Bind VM qualification to executable content, not the fixture's CA/origin.

    The only allowed fixture substitutions are the public CA, controller config,
    and the already-validated GRUB URL. Deployment verifies those inputs against
    the actual installation separately.
    """
    value = {
        "purpose": receipt["purpose"], "debianVersion": receipt["debianVersion"],
        "installerBuild": receipt["installerBuild"], "kernelAbi": receipt["kernelAbi"],
        "agentFiles": receipt["agentFiles"],
        "artifacts": {name: digest for name, digest in receipt["artifacts"].items()
                      if name not in (BOOT + "grub/grub.cfg", "lucia/lucia-overlay.cpio.gz")},
        "overlayFiles": {name: details for name, details in receipt["overlayFiles"].items()
                         if name not in ("etc/lucia/public-ca.crt", "etc/lucia/discovery.conf", "etc/lucia/installer-ca-bundle.crt")},
        "caPackage": receipt["packages"].get("ca-certificates"),
    }
    return sha(json.dumps(value, sort_keys=True, separators=(",", ":")).encode())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    builder = sub.add_parser("build")
    builder.add_argument("--output", required=True)
    builder.add_argument("--node-agent", "--agent-publish", dest="node_agent", required=True,
                         help="Full trusted linux-x64 publish, e.g. RELEASE/boot/node-agent-linux-x64")
    builder.add_argument("--ca-file", required=True)
    builder.add_argument("--ca-der-sha256", help="Optional expected lowercase SHA256 of public CA DER")
    builder.add_argument("--installation", action="store_true", help="Build the owner-approved installation profile; deployment still requires separate qualification")
    builder.add_argument("--server", default="https://spark-9423")
    builder.add_argument("--connect-address")
    builder.add_argument("--keyring", default=KEYRING)
    builder.add_argument("--replay", help="Previous bundle/evidence/downloads; offline, signatures rechecked")
    builder.add_argument("--boot-assets", help="Directory with the two hook scripts and grub.cfg.in")
    checker = sub.add_parser("verify", help="Offline local-receipt integrity check, not signed attestation")
    checker.add_argument("output")
    args = parser.parse_args()
    try:
        if args.command == "build":
            receipt = prepare_bundle(
                output=args.output, node_agent=args.node_agent, ca_file=args.ca_file,
                server=args.server, connect_address=args.connect_address,
                keyring=args.keyring, replay=args.replay, boot_assets=args.boot_assets,
                ca_der_sha256=args.ca_der_sha256, installation=args.installation)
            print(json.dumps({"bundle": str(Path(args.output).absolute()), "prepared": True,
                              "liveReady": False, "bootfile": BOOT + "bootnetx64.efi",
                              "overlaySha256": receipt["artifacts"]["lucia/lucia-overlay.cpio.gz"]}))
        else:
            receipt = verify_bundle(args.output)
            print(json.dumps({"integrityVerified": True, "liveReady": False,
                              "installerBuild": receipt["installerBuild"]}))
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(f"Preparation failed ({type(error).__name__}): {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
