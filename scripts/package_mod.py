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
payload['INSTALL.txt'] = ('''Mod-only update / 仅模组更新
Close the game on every PC. Extract into the game folder. Use the same build on all PCs.
所有电脑退出游戏后解压到游戏目录，所有参与者必须使用相同构建。
BepInEx, Doorstop, Python, .NET and Unity/game dependencies are NOT included.
本包不含第三方运行环境或游戏资源，必须先准备匹配的依赖。
No saves or runtime/room history are included. See THIRD_PARTY_NOTICES and RELEASE_REVIEW.
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
