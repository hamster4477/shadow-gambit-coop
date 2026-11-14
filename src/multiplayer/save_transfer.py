"""Room-authenticated, content-addressed mission save transport.

Only a locally published package is exposed. HTTP requests cannot choose a
filesystem path or install/overwrite a save. World loading is a separate step.
"""
import math
import hashlib
import json
import os
from pathlib import Path
import re
import threading
import uuid
from control_file import read_json

MAX_PACKAGE = 96 * 1024 * 1024 + 65536


def validate_rewind(value):
    if value is None:
        return None
    if (not isinstance(value, dict) or type(value.get('schema')) is not int or value['schema'] != 1
            or value.get('enemy') is not True or type(value.get('showEffect')) is not bool
            or type(value.get('intensity')) not in (int, float)
            or not math.isfinite(value['intensity']) or abs(value['intensity']) > 10000
            or not isinstance(value.get('names'), list) or not isinstance(value.get('states'), list)
            or len(value['names']) > 32 or len(value['names']) != len(value['states'])
            or any(not isinstance(n, str) or len(n) > 64 for n in value['names'])
            or any(type(n) is not int or not -2147483648 <= n <= 2147483647 for n in value['states'])
            or len(json.dumps(value, ensure_ascii=True)) > 4096):
        raise ValueError('Invalid enemy rewind metadata')
    return json.loads(json.dumps(value))


class SaveStore:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.lock = threading.Lock()

    def publish(self, data, *, mission, game_version, save_path, generation=None, world=None, rewind=None, entering=False, hub=False, preparation=False):
        if not 0 < len(data) <= MAX_PACKAGE or not isinstance(mission, str) or not mission:
            raise ValueError('Invalid published mission')
        if len(mission) > 160 or len(game_version) > 80 or len(save_path) > 220:
            raise ValueError('Invalid save metadata')
        generation = generation or uuid.uuid4().hex
        if not re.fullmatch('[0-9a-f]{32}', generation):
            raise ValueError('Invalid checkpoint generation')
        if world is not None and (not isinstance(world,str) or not re.fullmatch('[0-9a-f]{32}',world)):
            raise ValueError('Invalid checkpoint scene')
        if type(entering) is not bool: raise ValueError('Invalid mission entry flag')
        if type(preparation) is not bool or preparation and not hub:
            raise ValueError('Preparation requires a profile-only ship bundle')
        if type(hub) is not bool or hub and (mission != '-379511569' or save_path != 'profileData.save' or entering or rewind is not None):
            raise ValueError('Invalid ship transfer')
        rewind = validate_rewind(rewind)
        sha = hashlib.sha256(data).hexdigest()
        manifest = dict(schema=1, generation=generation, sha256=sha,
                        bytes=len(data), preparation=preparation, mission=mission, gameVersion=game_version, savePath=save_path, world=world, rewind=rewind, entering=entering, hub=hub)
        with self.lock:
            self.directory.mkdir(parents=True, exist_ok=True)
            target = self.directory / (sha + '.zip')
            temp = self.directory / (sha + '.' + uuid.uuid4().hex + '.new')
            try:
                with temp.open('xb') as stream:
                    stream.write(data)
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temp, target)
            finally:
                temp.unlink(missing_ok=True)
            pending = self.directory / ('manifest.' + uuid.uuid4().hex + '.new')
            try:
                pending.write_text(json.dumps(manifest, ensure_ascii=True), encoding='ascii')
                os.replace(pending, self.directory / 'manifest.json')
            finally:
                pending.unlink(missing_ok=True)
        return manifest

    def manifest(self):
        path = self.directory / 'manifest.json'
        try:
            value = read_json(path, 4096)
            if (value.get('schema') != 1 or not re.fullmatch('[0-9a-f]{64}', value.get('sha256', ''))
                    or not re.fullmatch('[0-9a-f]{32}', value.get('generation', ''))
                    or type(value.get('bytes')) is not int or not 0 < value['bytes'] <= MAX_PACKAGE):
                return None
            return value
        except (ValueError, TypeError, OSError):
            return None

    def package(self, sha):
        if not re.fullmatch('[0-9a-f]{64}', sha):
            return None
        value = self.manifest()
        if value is None or sha != value['sha256']:
            return None
        path = self.directory / (sha + '.zip')
        if not path.is_file() or path.stat().st_size != value['bytes']:
            return None
        with path.open('rb') as stream:
            data = stream.read(MAX_PACKAGE + 1)
        if len(data) != value['bytes'] or hashlib.sha256(data).hexdigest() != sha:
            return None
        return data


def serve_save(handler):
    prefix = handler.server.room + '/save/'
    if not handler.path.startswith(prefix):
        return False
    store = getattr(handler.server, 'save_store', None)
    if store is None:
        handler.send_error(404)
        return True
    suffix = handler.path[len(prefix):]
    mime = 'application/json'
    if suffix == 'manifest':
        value = store.manifest()
        data = json.dumps(value).encode('ascii') if value is not None else None
    elif re.fullmatch(r'[0-9a-f]{64}\.zip', suffix):
        data = store.package(suffix[:-4])
        mime = 'application/zip'
    else:
        data = None
    if data is None:
        handler.send_error(404)
        return True
    handler.send_response(200)
    handler.send_header('Content-Type', mime)
    handler.send_header('Content-Length', str(len(data)))
    handler.send_header('Cache-Control', 'no-store')
    handler.send_header('Referrer-Policy', 'no-referrer')
    handler.end_headers()
    handler.wfile.write(data)
    return True
