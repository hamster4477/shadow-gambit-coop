"""Immutable save transfer payload for the upcoming session protocol.

No automatic account selection, extraction, native load, or network publication.
The caller must provide the host's active profile and a completed native save.
"""
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import zipfile

MAX_FILE = 32 * 1024 * 1024
MAX_TOTAL = 96 * 1024 * 1024
MAX_FILES = 32


def allowed(name):
    return name in ('profileData.save', 'profileData.save1', 'profileData.save2') or bool(
        re.fullmatch(r'(manualSave|quickSave)/[A-Za-z0-9_-]{1,180}\.save', name))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def build(profile, mission_files, *, hub=False):
    profile = Path(profile).resolve(strict=True)
    names = ['profileData.save']
    names += [n for n in ('profileData.save1', 'profileData.save2') if (profile / n).is_file()]
    selected = list(mission_files)
    if (not hub and not selected) or (hub and selected) or not all(allowed(n) and '/' in n for n in selected):
        raise ValueError('Choose completed mission save files')
    names += selected
    if len(names) > MAX_FILES or len(set(names)) != len(names):
        raise ValueError('Invalid save file count')
    entries = {}
    stamps = {}
    for name in names:
        path = profile.joinpath(*PurePosixPath(name).parts)
        if not path.resolve(strict=True).is_relative_to(profile) or path.is_symlink():
            raise ValueError('Save path escapes active profile')
        stat = path.stat()
        if not 0 < stat.st_size <= MAX_FILE:
            raise ValueError('Invalid save size')
        stamps[name] = (stat.st_size, stat.st_mtime_ns)
        entries[name] = path.read_bytes()
        if len(entries[name]) != stat.st_size:
            raise ValueError('Save changed during export')
    if sum(map(len, entries.values())) > MAX_TOTAL:
        raise ValueError('Save bundle too large')
    # Native saves may update the profile and scene separately; the caller must
    # additionally wait for the native save-completed event before calling us.
    for name, stamp in stamps.items():
        stat = (profile / name).stat()
        if (stat.st_size, stat.st_mtime_ns) != stamp:
            raise ValueError('Save changed during export')
    manifest = {'schema': 1, 'hub': hub, 'files': [
        {'path': n, 'size': len(data), 'sha256': digest(data)} for n, data in entries.items()]}
    out = io.BytesIO()
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
        archive.writestr('manifest.json', json.dumps(manifest, sort_keys=True))
    data = out.getvalue()
    verify(data, digest(data))
    return data, digest(data)


def verify(data, expected_hash):
    if len(data) > MAX_TOTAL + 65536 or digest(data) != expected_hash:
        raise ValueError('Bundle checksum or size mismatch')
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        info = archive.infolist()
        names = [i.filename for i in info]
        if not 2 <= len(names) <= MAX_FILES + 1 or len(set(names)) != len(names):
            raise ValueError('Duplicate entries or invalid count')
        if 'manifest.json' not in names or archive.getinfo('manifest.json').file_size > 16384:
            raise ValueError('Missing or oversized manifest')
        if any(n != 'manifest.json' and not allowed(n) for n in names):
            raise ValueError('Unexpected save entry')
        if any(i.file_size > MAX_FILE or i.flag_bits & 1 for i in info) or sum(i.file_size for i in info) > MAX_TOTAL + 16384:
            raise ValueError('Invalid expanded size or encryption')
        manifest = json.loads(archive.read('manifest.json'))
        if manifest.get('schema') != 1 or not isinstance(manifest.get('files'), list):
            raise ValueError('Unsupported manifest')
        rows = manifest['files']
        if len(rows) != len(names) - 1 or {r['path'] for r in rows} != set(names) - {'manifest.json'}:
            raise ValueError('Manifest entries mismatch')
        result = {}
        for row in rows:
            content = archive.read(row['path'])
            if not content or len(content) != row['size'] or digest(content) != row['sha256']:
                raise ValueError('Save entry checksum mismatch')
            result[row['path']] = content
        hub = manifest.get('hub', False)
        if type(hub) is not bool or (hub and any('/' in n for n in result)):
            raise ValueError('Invalid hub bundle')
        if 'profileData.save' not in result or (not hub and not any('/' in n for n in result)):
            raise ValueError('Bundle needs profile and mission save')
        return result
