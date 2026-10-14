"""Build install-source.tar.gz (+ .sha256) from the repo's bootstrap payload file list, byte-for-byte."""
import hashlib, io, json, pathlib, sys, tarfile

repo = pathlib.Path(sys.argv[1]).resolve()
out = pathlib.Path(sys.argv[2]).resolve()
sys.path.insert(0, str(repo / "tools/desktop"))
import bootstrap

files = {name: (repo / name).read_bytes() for name in sorted(bootstrap.FILES)}
manifest = {"schema_version": bootstrap.SCHEMA, "version": bootstrap.VERSION,
            "files": [{"path": name, "sha256": hashlib.sha256(data).hexdigest()} for name, data in files.items()]}
files["manifest.json"] = (json.dumps(manifest, indent=2) + "\n").encode()
buffer = io.BytesIO()
with tarfile.open(fileobj=buffer, mode="w:gz", format=tarfile.PAX_FORMAT) as tar:
    for name, data in files.items():
        info = tarfile.TarInfo(name)
        info.size, info.mode, info.mtime = len(data), 0o644, 0
        tar.addfile(info, io.BytesIO(data))
(out / "install-source.tar.gz").write_bytes(buffer.getvalue())
(out / "install-source.sha256").write_text(hashlib.sha256(buffer.getvalue()).hexdigest() + "\n")
print(json.dumps({name: hashlib.sha256(data).hexdigest()[:12] for name, data in files.items() if name != "manifest.json"}))
