"""
Extract, or put back, the single native binary inside a RID-specific .NET tool package.

Authenticode has to be applied to the binary *inside* the .nupkg, and it cannot be
applied before packing: `dotnet pack -r <rid>` re-runs the Native AOT publish and
overwrites whatever was there, and `--no-build` does not change that. Both were
measured — a deliberately modified publish output came back with its original hash
in the produced package, with and without --no-build. So the order is pack, extract,
sign, put back.

  extract <nupkg> <dest-dir>   writes the binary and prints its path
  replace <nupkg> <binary>     rewrites the package with that binary in place
"""
import os, sys, zipfile

def entry_of(z):
    """The tool binary: the one file under tools/ with no extension, or .exe."""
    hits = [n for n in z.namelist()
            if n.startswith("tools/") and (n.endswith(".exe") or "." not in n.rsplit("/", 1)[-1])]
    if len(hits) != 1:
        raise SystemExit(f"expected exactly one tool binary under tools/, found {hits} — refusing to guess")
    return hits[0]

def extract(pkg, dest):
    with zipfile.ZipFile(pkg) as z:
        name = entry_of(z)
        out = os.path.join(dest, os.path.basename(name))
        os.makedirs(dest, exist_ok=True)
        with open(out, "wb") as f:
            f.write(z.read(name))
    print(out)

def replace(pkg, binary):
    with open(binary, "rb") as f:
        data = f.read()
    tmp = pkg + ".new"
    with zipfile.ZipFile(pkg) as zin:
        name = entry_of(zin)
        if len(data) <= len(zin.read(name)):
            raise SystemExit(f"{binary} is not larger than the unsigned original — signing did not add a signature")
        with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zout:
            for item in zin.infolist():
                zout.writestr(item, data if item.filename == name else zin.read(item.filename))
    os.replace(tmp, pkg)
    print(f"replaced {name} in {os.path.basename(pkg)} ({len(data)} bytes)")

if __name__ == "__main__":
    cmd, pkg, arg = sys.argv[1], sys.argv[2], sys.argv[3]
    {"extract": extract, "replace": replace}[cmd](pkg, arg)
