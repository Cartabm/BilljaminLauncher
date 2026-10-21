#!/usr/bin/env python3
"""Create launcher-manifest.json for a published BilljaminLauncher.exe."""
import hashlib, json, pathlib, sys

def sha256(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()

def main():
    if len(sys.argv) != 4:
        raise SystemExit('Usage: python tools/make_launcher_release.py <BilljaminLauncher.exe> <version> <output_folder>')
    exe = pathlib.Path(sys.argv[1]).resolve()
    version = sys.argv[2].strip().lstrip('vV')
    out = pathlib.Path(sys.argv[3]).resolve()
    if not exe.is_file(): raise SystemExit(f'Launcher executable not found: {exe}')
    if not version or len(version) > 32: raise SystemExit('Provide a launcher version, e.g. 1.2.0')
    out.mkdir(parents=True, exist_ok=True)
    target = out / 'BilljaminLauncher.exe'
    if exe != target: target.write_bytes(exe.read_bytes())
    manifest = {'version': version, 'file': 'BilljaminLauncher.exe', 'sha256': sha256(target)}
    (out / 'launcher-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(f'Created {target} and {out / "launcher-manifest.json"}')
    print(f'SHA-256: {manifest["sha256"]}')

if __name__ == '__main__': main()
