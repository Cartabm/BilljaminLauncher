#!/usr/bin/env python3
"""Create game.zip and manifest.json from a Godot Windows export folder.
Usage: python tools/make_release.py <export_folder> <version> <output_folder>
"""
import hashlib
import json
import pathlib
import sys
import zipfile

GAME_EXE = 'TheTowerOfBilljamin.exe'


def sha256_file(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    if len(sys.argv) != 4:
        raise SystemExit('Usage: python tools/make_release.py <export_folder> <version> <output_folder>')
    source = pathlib.Path(sys.argv[1]).resolve()
    version = sys.argv[2].strip()
    out = pathlib.Path(sys.argv[3]).resolve()
    if not source.is_dir() or not version or len(version) > 80:
        raise SystemExit('Provide an existing export folder and a non-empty version (max 80 chars).')
    if not (source / GAME_EXE).is_file():
        raise SystemExit(f'Expected {GAME_EXE} in the export folder. Rename the exported .exe or edit GAME_EXE.')
    out.mkdir(parents=True, exist_ok=True)
    files = {}
    paths = []
    for path in sorted(source.rglob('*')):
        if not path.is_file():
            continue
        if path.is_symlink():
            raise SystemExit(f'Symlinks are not supported: {path}')
        rel = path.relative_to(source).as_posix()
        if rel == '.installed-version':
            raise SystemExit('Reserved filename .installed-version found in export.')
        files[rel] = sha256_file(path)
        paths.append((path, rel))
    archive = out / 'game.zip'
    with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as z:
        for path, rel in paths:
            z.write(path, rel)
    manifest = {'version': version, 'archive_sha256': sha256_file(archive), 'files': files}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(f'Created {archive} and {out / "manifest.json"} for {version} ({len(files)} files)')


if __name__ == '__main__':
    main()
