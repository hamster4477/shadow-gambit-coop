"""Package only this project's output. Never vendor game/engine/framework files."""
import argparse
import hashlib
import json
import re
from pathlib import Path, PurePosixPath
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--variant', choices=('two-player', 'multiplayer'), required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
build = repo / 'artifacts' / 'build' / args.variant
source = repo / 'src' / args.variant
plugin_source = (source / 'MoveProbe.cs').read_text(encoding='utf-8-sig')
match = re.search(r'\[BepInPlugin\("local\.shadowgambit\.moveprobe",\s*"[^"]+",\s*"([0-9]+\.[0-9]+\.[0-9]+)"\)\]', plugin_source)
if not match:
    raise ValueError('Cannot determine plugin version from the selected source variant')
version = match.group(1)
name = f'opensource-{args.variant}-{version}'
payload = {}
for item in json.loads((source / 'runtime-files.json').read_text(encoding='utf-8-sig')):
    path = PurePosixPath(item)
    if path.is_absolute() or '..' in path.parts or ':' in item or '\\' in item:
        raise ValueError('Unsafe runtime path')
    if path.suffix not in ('.cs', '.ps1', '.py', '.vbs', '.cmd', '.json', '.wav'):
        raise ValueError('Unexpected runtime file type')
    payload['ShadowGambitCoop/' + item] = (build / item).read_bytes()
payload['BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll'] = (build / 'staging/ShadowGambitCoop.MoveProbe.dll').read_bytes()
payload['ShadowGambitCoop/SteamLauncherHost.exe'] = (build / 'SteamLauncherHost.exe').read_bytes()
sha = lambda value: hashlib.sha256(value).hexdigest().upper()
plugin_hash = sha(payload['BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll'])
metadata = dict(version=version, buildId=name, dllSha256=plugin_hash, launchModes=True,
                liveLobbyJoin=True, diagnostic=False, multiplayerTrial=args.variant == 'multiplayer',
                source='Built from the MIT source export; third-party dependencies are not bundled.')
payload['ShadowGambitCoop/observation-build.json'] = json.dumps(metadata, indent=2).encode('utf-8-sig')
for notice in ('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md', 'RELEASE_REVIEW.md', 'SECURITY.md'):
    payload['ShadowGambitCoop/' + notice] = (repo / notice).read_bytes()
manifest = dict(buildId=name, dllSha256=plugin_hash, nativeGameplay='pending',
                entries={key: sha(value) for key, value in payload.items()})
payload['ShadowGambitCoop/unified-test-manifest.json'] = json.dumps(manifest, indent=2).encode('utf-8-sig')
if (repo / 'FORUM-POST.md').is_file():
    payload['FORUM-POST.md'] = (repo / 'FORUM-POST.md').read_bytes()
payload['INSTALL.txt'] = ('''Shadow Gambit Co-op - mod-only package / 纯模组包

1. Close the game on every PC. / 所有电脑先完全退出游戏。
2. Extract this ZIP into the game folder. / 把本 ZIP 解压到游戏根目录。
3. Check the environment / 检查环境:
     powershell -NoProfile -ExecutionPolicy Bypass -File .\\ShadowGambitCoop\\Check-Environment.ps1
4. Install anything missing, once / 缺依赖时运行一次:
     powershell -NoProfile -ExecutionPolicy Bypass -File .\\ShadowGambitCoop\\Install-Dependencies.ps1
   It downloads only BepInEx 6.0.0-be.682 and CPython 3.13.9 from their official sites,
   verifies pinned SHA-256 values, backs up what it replaces and can roll back.
   只从官方站点下载这两个依赖，校验固定哈希，覆盖前会备份并可回退。
   Offline machines / 离线机器: -OfflineCache <folder holding those two ZIP files>
5. Everyone must use the same build. / 所有参与者必须使用同一构建。

BepInEx is not redistributed in this package; it is fetched from builds.bepinex.dev.
本包不再分发 BepInEx，改为从官方站点获取。
BepInEx downloads the Unity base libraries itself on the first game launch (needs network once).
Unity 基础库由 BepInEx 在首次启动游戏时自行下载，需要联网一次。
No saves or runtime/room history are included. See THIRD_PARTY_NOTICES and RELEASE_REVIEW.
An English announcement you can post on a forum is in FORUM-POST.md.
''').encode('utf-8-sig')
target = repo / 'artifacts' / f'ShadowGambitCoop-{name}-mod-only.zip'
with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
    for path, value in payload.items():
        if any(part in ('runtime', 'interop', 'backups', 'save-backups', 'python', 'dotnet', 'unity-libs', 'core') for part in PurePosixPath(path).parts):
            raise ValueError('Dependency/private data unexpectedly included')
        archive.writestr(path, value)
with zipfile.ZipFile(target) as archive:
    assert archive.testzip() is None
    for path, digest in manifest['entries'].items():
        assert sha(archive.read(path)) == digest
print(f'{target.name}: {target.stat().st_size} bytes; SHA256 {sha(target.read_bytes())}')
