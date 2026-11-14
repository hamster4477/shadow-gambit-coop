"""Serve a LAN controller for the host's bound crew member."""
import argparse
import json
import html
import hashlib
import hmac
import threading
import time
from http.client import HTTPConnection
import ipaddress
import math
import os
from pathlib import Path
import re
import secrets
import select
import socket
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

from send_move import exchange
from save_transfer import SaveStore, serve_save
from session_broker import SessionBroker, serve_session
from lobby_broker import serve_lobby
from control_file import atomic
from gateway_pairing import RoomPairing, serve_pairing
from native_tcp import NativeServer

DIRECTIONS = {"up": "↑ 上", "left": "← 左", "right": "右 →", "down": "↓ 下"}


# The shipping catalog and compiled C# validator are checked together in the
# wire regression. New skills must not be admitted by only one transport layer.
_skill_catalog = json.loads(Path(__file__).with_name('skill-protocol.json').read_text(encoding='utf-8'))
if _skill_catalog.get('schema') != 1:
    raise ValueError('Unsupported skill protocol catalog')
INTERACTION_CODES = frozenset(x['code'] for x in _skill_catalog['skills'])
INTERACTION_GEOMETRY = frozenset(x['code'] for x in _skill_catalog['skills'] if x['geometry'])
INTERACTION_NULL_TARGET = frozenset(x['code'] for x in _skill_catalog['skills'] if x['nullTarget'])


def valid_point_payload(payload):
    if not payload.isascii() or not 0 < len(payload) <= 4641:
        return False
    fields = payload.split(' ')
    versioned = len(fields)>=7 and fields[-3]=='Q'
    if versioned:
        if not all(re.fullmatch(r'[0-9]{1,19}',v) and 0<int(v)<2**63 for v in fields[-2:]): return False
        fields=fields[:-3]
    if len(fields) not in (4, 5, 6, 7, 8, 10):
        return False
    ticket, identity, session, *args = fields
    if not (re.fullmatch('[0-9a-f]{32}', ticket) and re.fullmatch('[0-9A-F]{64}', identity)
            and re.fullmatch('[0-9a-f]{32}', session)):
        return False
    if len(args)==2 and args[0] in ('SELECT','RELEASE'):
        ids=args[1].split(',')
        return versioned and 1<=len(ids)<=64 and len(set(ids))==len(ids) and all(re.fullmatch('[0-9A-F]{64}',v) for v in ids)
    if len(payload)>(545 if versioned else 417): return False
    if args in (['CROUCH'], ['WALK'], ['CLAIM']):
        return True
    if len(args) == 2 and args[0] == 'MIMIC':
        return versioned and bool(re.fullmatch(r'0|[1-9][0-9]{0,9}', args[1])) and int(args[1]) < 2**31
    if len(args) == 2 and args[0] == 'READY':
        return valid_sequence(args[1])
    if len(args) == 5 and args[0] in ('GW','GC','RW','RC') and args[4] in ('LAND','SHALLOW','DEEP'):
        return valid_coordinates(args[1:4])
    if len(args) == 4 and args[0] in ('GW', 'GC', 'RW', 'RC', 'ThrowNoise', 'ThrowBush'):
        args = args[1:]
        return valid_coordinates(args)
    if args[0] == 'USE':
        return len(args)==7 and args[1] in INTERACTION_CODES and valid_interaction_key(args[2], args[1]) and valid_coordinates(args[3:6]) and bool(re.fullmatch(r'[0-9]{1,2}',args[6])) and int(args[6])<=15
    if args[0] == 'BODY':
        return len(args)==3 and ((args[1] in ('P','PR','M','MR') and re.fullmatch(r'-?\d{1,10}',args[2]) and -(2**31)<=int(args[2])<2**31) or args[1:] == ['D','0'])
    if args[0] == 'HIT':
        if len(args) not in (3, 7) or not re.fullmatch(r'-?\d{1,10}', args[2]) or not -(2**31) <= int(args[2]) < 2**31:
            return False
        if len(args) == 7:
            return args[1] in ('K', 'L', 'KR', 'LR') and valid_sequence(args[3]) and valid_coordinates(args[4:])
        return args[1] in ('K', 'L', 'KR', 'LR', 'BlinkKill', 'SnipeKill', 'Gun', 'BlinkKillLong', 'BlinkKnockout', 'BlinkKnockoutLong', 'SnipeKnockout')
    return len(args) == 3 and valid_coordinates(args)


def valid_interaction_key(value, code):
    def entity(s):
        return s=='-' or bool(re.fullmatch(r'[po][0-9A-F]{64}',s)) or bool(re.fullmatch(r'[eu]-?\d{1,10}',s)) and -(2**31)<=int(s[1:])<2**31
    if value == "n": return code in INTERACTION_NULL_TARGET
    if entity(value): return True
    if len(value)>384 or code not in INTERACTION_GEOMETRY: return False
    f=value.split('~')
    return len(f)==13 and entity(f[0]) and f[1] in ('0','1') and f[2] in tuple(str(n) for n in range(8)) and entity(f[3]) and valid_coordinates(f[4:])


def valid_sequence(value):
    return bool(re.fullmatch(r'[0-9]{1,10}', value) and 0 < int(value) < 2**31)


def valid_coordinates(args):
    try:
        return all('_' not in v and math.isfinite(float(v)) and abs(float(v)) <= 1000000 for v in args)
    except ValueError:
        return False


def ask_game(message, port):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(0.6)
        sock.connect(("127.0.0.1", port))
        for _ in range(3):
            reply = exchange(sock, message)
            if reply:
                return reply
    return ""


def release_health(project=None):
    """A room advertises the verified installed binary, never just a version label."""
    project = Path(project) if project is not None else Path(__file__).resolve().parent
    try:
        meta = json.loads((project / 'observation-build.json').read_text(encoding='utf-8-sig'))
        if not isinstance(meta, dict):
            return 'COOP_BUILD_INVALID'
        binary = project.parent / 'BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll'
        digest = hashlib.sha256(binary.read_bytes()).hexdigest().upper()
        if digest != str(meta.get('dllSha256', '')).upper():
            return 'COOP_BUILD_INVALID'
        return 'COOP_ROOM_084 ' + digest
    except (OSError, ValueError, TypeError):
        return 'COOP_BUILD_INVALID'


class Remote(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    disable_nagle_algorithm = True
    def setup(self):
        super().setup()
        self.connection.settimeout(5)

    def body(self, limit):
        lengths = self.headers.get_all('Content-Length', [])
        if len(lengths) != 1 or self.headers.get('Transfer-Encoding'):
            raise ValueError('Unsupported body framing')
        size = int(lengths[0])
        if not 0 < size <= limit:
            raise ValueError('Invalid body size')
        raw = self.rfile.read(size)
        if len(raw) != size:
            raise ValueError('Truncated body')
        return raw.decode('ascii')

    def plain(self, value):
        body = value.encode("ascii")
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def allowed(self):
        if self.path != self.server.room:
            self.send_error(404)
            return False
        return True

    def page(self, message, ticket=None):
        action = '<button>刷新目标</button>'
        method = "get"
        if ticket:
            action = f'<input type="hidden" name="ticket" value="{html.escape(ticket)}"><button>命令 B 移动</button>'
            method = "post"
        pad = ""
        control = ask_game("CONTROL", self.server.game_port)
        if control.startswith("CONTROL "):
            nonce = html.escape(control[8:], quote=True)
            buttons = "".join(f'<button name="direction" value="{d}">{label}</button>'
                              for d, label in DIRECTIONS.items())
            pad = (f'<p>按房主镜头方向，让 B 向附近移动约 3 个游戏单位。</p>'
                   f'<form method="post" action="{self.server.room}">'
                   f'<input type="hidden" name="ticket" value="{nonce}">'
                   f'<div class="pad">{buttons}</div></form>')
        body = (f'<!doctype html><html lang="zh"><meta charset="utf-8">'
                '<meta name="viewport" content="width=device-width,initial-scale=1">'
                '<title>影子诡局 · 船员 B</title><style>'
                'body{font:18px system-ui;max-width:30em;margin:2em auto;padding:0 1em}'
                'button{padding:.75em;font-size:1em}.pad{display:grid;grid-template-columns:repeat(3,1fr);gap:.4em}'
                '[value=up],[value=down]{grid-column:2}[value=left]{grid-column:1}[value=right]{grid-column:3}'
                '</style><h1>船员 B</h1>'
                f'<p>{html.escape(message)}</p>'
                f'{pad}'
                f'<form method="{method}" action="{self.server.room}">{action}</form></html>').encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def peer(self):
        value = self.headers.get('X-Coop-Peer')
        if value is not None and not re.fullmatch(r'[0-9a-f]{32}', value):
            raise ValueError('Invalid multiplayer identity')
        return value

    def game(self, command):
        peer = self.peer()
        if not peer:
            return 'MULTIPLAYER_ID_REQUIRED'
        broker = getattr(self.server, 'session_broker', None)
        if broker is not None:
            broker.touch_peer(peer)
        return ask_game('PEER ' + peer + ' ' + command, self.server.game_port)

    def do_GET(self):
        if self.path.startswith(self.server.room + '/'):
            try: self.peer()
            except ValueError:
                self.send_error(400)
                return
        if serve_lobby(self):
            return
        if serve_session(self):
            return
        if serve_save(self):
            return
        if self.path == self.server.room + "/health":
            self.plain(release_health())
            return
        if self.path == self.server.room + "/state":
            seen = self.headers.get("X-Coop-Seen")
            if seen is not None and not re.fullmatch(r'[0-9a-f]{32} [1-9][0-9]{0,9} [RHIP]', seen):
                self.send_error(400)
                return
            self.plain(self.game("STATE" if seen is None else "STATE "+seen) or "NO_REPLY")
            return
        if self.path == self.server.room + "/point":
            reply = self.game("CONTROL")
            self.plain(reply[8:] if reply.startswith("CONTROL ") else "NOT_READY")
            return
        # Browsers can append '?' when submitting an empty GET form.
        self.path = self.path.removesuffix("?")
        if not self.allowed():
            return
        reply = ask_game("TARGET", self.server.game_port)
        if reply.startswith("TARGET "):
            self.page("房主已经标记落点。等 A 走开后，点击下方按钮。", reply[7:])
        else:
            self.page("方向键可连续发令。若未显示方向键，请房主选 B 按 F7，再选 A 按 F8，然后刷新。")

    def do_POST(self):
        if self.path.startswith(self.server.room + '/'):
            try: self.peer()
            except ValueError:
                self.send_error(400)
                return
        if serve_pairing(self):
            return
        if serve_lobby(self):
            return
        if serve_session(self):
            return
        if self.path == self.server.room + "/point":
            try:
                payload = self.body(4641)
                if not valid_point_payload(payload):
                    raise ValueError()
            except TimeoutError:
                self.send_error(408)
                return
            except ValueError:
                self.send_error(400)
                return
            verb = "ORDER " if self.headers.get("X-Coop-Next") == "1" else "POINT "
            self.plain(self.game(verb + payload) or "NO_REPLY")
            return
        if not self.allowed():
            return
        try:
            params = parse_qs(self.body(128))
            ticket, = params["ticket"]
            if len(ticket) != 32 or any(c not in "0123456789abcdef" for c in ticket):
                raise ValueError()
            direction = None
            if "direction" in params:
                direction, = params["direction"]
                if direction not in DIRECTIONS:
                    raise ValueError()
        except TimeoutError:
            self.send_error(408)
            return
        except (ValueError, KeyError):
            self.send_error(400)
            return
        request = f"STEP {ticket} {direction}" if direction else "MOVE " + ticket
        reply = ask_game(request, self.server.game_port)
        if reply == "ACCEPTED":
            self.page("游戏已收到移动指令。")
        elif direction and reply:
            self.page("指令未执行。请确认 A 已选中、任务未暂停，再试一个方向。")
        elif reply:
            self.page("当前目标已经失效。请房主重新按 F8，再刷新目标。")
        else:
            self.page("游戏没有回执。请检查游戏是否运行并处于任务中。")

    def log_message(self, fmt, *args):
        pass  # The URL contains the session secret.


def private_ipv4(value):
    ip = ipaddress.IPv4Address(value)
    return any(ip in block for block in PRIVATE_NETWORKS)


PRIVATE_NETWORKS = tuple(map(ipaddress.ip_network, ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "25.0.0.0/8", "26.0.0.0/8"]))


def snapshot_packets(nonce, serial, body):
    """Bounded, atomic snapshot fragments for FRAME2 clients."""
    if not 0 < len(body) <= 32768 or not body.isascii():
        return ()
    count = (len(body) + 1023) // 1024
    digest = hashlib.sha256(body).hexdigest().encode('ascii')
    return tuple(b'PART ' + nonce + b' ' + serial + b' ' + str(index).encode('ascii') + b' ' +
                 str(count).encode('ascii') + b' ' + str(len(body)).encode('ascii') + b' ' + digest +
                 b' ' + body[index*1024:(index+1)*1024] for index in range(count))


class LanFrames:
    """Optional read-only UDP snapshot service. Commands always use HTTP."""

    def __init__(self, address, port, room, game_port, *, fixture=False):
        self.fixture = fixture and address == "127.0.0.1"
        if not self.fixture and not private_ipv4(address):
            raise ValueError("LAN snapshots require a private IPv4 bind")
        self.room = room.removeprefix("/").encode("ascii")
        self.key = secrets.token_bytes(32)
        self.game_port = game_port
        self.stop = threading.Event()
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
                self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            self.sock.bind((address, port))
            self.sock.setblocking(False)
            self.thread = threading.Thread(target=self.run, name="coop-lan-frames", daemon=True)
            self.thread.start()
        except BaseException:
            self.sock.close()
            raise

    def close(self):
        self.stop.set()
        self.thread.join(timeout=1)
        self.sock.close()

    def parse(self, packet, peer):
        if len(packet) > 192 or not (private_ipv4(peer[0]) or self.fixture and peer[0] == "127.0.0.1"):
            return None
        fields = packet.split(b" ")
        if len(fields) not in (3, 5, 8) or not hmac.compare_digest(fields[1], self.room):
            return None
        nonce = fields[2]
        if not re.fullmatch(rb"[0-9a-f]{32}", nonce):
            return None
        # A sender must receive our cookie at this IP and UDP port before it
        # can obtain larger snapshots. No session allocation for HELLO.
        binding = socket.inet_aton(peer[0]) + peer[1].to_bytes(2, "big") + self.room + nonce
        cookie = hmac.digest(self.key, binding, "sha256")[:16].hex().encode("ascii")
        if fields[0] == b"HELLO" and len(fields) == 3:
            self.sock.sendto(b"COOKIE " + nonce + b" " + cookie, peer)
            return None
        if fields[0] not in (b"FRAME", b"FRAME2") or len(fields) not in (5, 8) or not hmac.compare_digest(fields[3], cookie):
            return None
        if not re.fullmatch(rb"[1-9][0-9]{0,9}", fields[4]) or int(fields[4]) >= 2**31:
            return None
        request = b"STATE"
        if len(fields) == 8:
            if not re.fullmatch(rb"[0-9a-f]{32}", fields[5]) or not re.fullmatch(rb"[1-9][0-9]{0,9}", fields[6]) or int(fields[6]) >= 2**31 or fields[7] not in (b"R", b"H", b"I", b"P"):
                return None
            request += b" " + b" ".join(fields[5:])
        return peer, nonce, fields[4], b"PEER " + nonce + b" " + request, fields[0] == b"FRAME2"

    def run(self):
        game = active = None
        pending = {}
        deadline = 0
        latest = {}
        try:
            while not self.stop.is_set():
                now = time.monotonic()
                if active is not None and now >= deadline:
                    # Retire the loopback port so a late reply cannot acquire a
                    # later request ID. Normal replies reuse the same socket.
                    game.close()
                    game = active = None
                if pending and active is None:
                    stream = next(iter(pending))
                    item = pending.pop(stream)
                    try:
                        if game is None:
                            game = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                            game.connect(("127.0.0.1", self.game_port))
                            game.setblocking(False)
                        game.send(item[3])
                        active = item
                        deadline = now + 0.25
                    except OSError:
                        if game is not None:
                            game.close()
                        game = None
                sockets = [self.sock] if active is None else [self.sock, game]
                ready, _, _ = select.select(sockets, [], [], 0.1)
                if active is not None and game in ready:
                    try:
                        body = game.recv(32769)
                        if active[4]:
                            for packet in snapshot_packets(active[1], active[2], body):
                                self.sock.sendto(packet, active[0])
                        elif 0 < len(body) <= 2048 and body.isascii():
                            self.sock.sendto(b"FRAME " + active[1] + b" " + active[2] + b" " + body, active[0])
                    except OSError:
                        game.close()
                        game = None
                    active = None
                if self.sock in ready:
                    for _ in range(32):
                        try:
                            packet, peer = self.sock.recvfrom(193)
                            item = self.parse(packet, peer)
                        except OSError:
                            break
                        if item is None:
                            continue
                        stream = (peer, item[1])
                        serial = int(item[2])
                        if serial <= latest.get(stream, (0, now))[0]:
                            continue
                        latest[stream] = (serial, now)
                        pending[stream] = item  # Latest per peer, fair order across peers.
                    for stale in [key for key, value in latest.items() if now-value[1] > 30]:
                        latest.pop(stale, None)
                        pending.pop(stale, None)
        finally:
            if game is not None:
                game.close()


def public_endpoint(value):
    value = value.strip()
    if '://' not in value:
        value = 'http://' + value
    if len(value) > 2048 or re.search(r'[\s\\%]', value):
        raise ValueError('地址含空白或不支持的字符')
    parts = urlsplit(value)
    if (parts.scheme not in ('http', 'https') or not parts.hostname or parts.username is not None
            or parts.password is not None or parts.path not in ('', '/') or '?' in value or '#' in value
            or parts.port is not None and not 1 <= parts.port <= 65535):
        raise ValueError('请输入 http://域名:端口，仅需地址，不要附带房间路径')
    host = parts.hostname
    if ':' in host:
        raise ValueError('请使用域名或 IPv4 地址')
    try:
        address = ipaddress.IPv4Address(host)
    except ipaddress.AddressValueError:
        host = host.encode('idna').decode('ascii')
        if len(host) > 253 or not all(re.fullmatch(r'[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?', label)
                                     for label in host.rstrip('.').split('.')):
            raise ValueError('域名格式不正确')
    else:
        if not 0 < address.packed[0] < 224:
            raise ValueError('地址不能是未指定、组播或广播地址')
    authority = host + (':' + str(parts.port) if parts.port is not None else '')
    return parts.scheme + '://' + authority


def lan_room(directory, address, renew=False):
    # Called after binding the port, so only the active listener can rotate it.
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / (str(ipaddress.IPv4Address(address)) + '.room')
    if not renew and path.exists():
        token = path.read_text(encoding='ascii').strip()
        if re.fullmatch(r'[A-Za-z0-9_-]{24}', token):
            return '/' + token
    token = secrets.token_urlsafe(18)
    temp = path.with_suffix('.new')
    temp.write_text(token, encoding='ascii')
    temp.replace(path)
    return '/' + token


class RoomServer(ThreadingHTTPServer):
    allow_reuse_address = False

    def server_bind(self):
        if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


class HostHealth(BaseHTTPRequestHandler):
    """Loopback only. Repair the local room file from the live listener."""
    def do_POST(self):
        port = self.server.server_port
        if (self.path != '/host/verify' or self.headers.get('Host') != f'127.0.0.1:{port}'
                or self.headers.get('Origin') or self.headers.get('X-Coop-Host') != 'verify'
                or self.headers.get('Content-Length') != '0' or self.headers.get('Transfer-Encoding')):
            self.send_error(404)
            return
        token = self.server.live.room[1:]
        path = self.server.room_file
        try:
            with self.server.repair_lock:
                if not path.exists() or path.read_bytes().strip() != token.encode('ascii'):
                    temporary = path.with_suffix('.repair')
                    temporary.write_text(token, encoding='ascii')
                    temporary.replace(path)
        except OSError:
            self.send_error(503)
            return
        body = hashlib.sha256(token.encode('ascii')).hexdigest().encode('ascii')
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


class PairingWindow:
    """Accept launch requests only through this installation's local filesystem."""
    def __init__(self, directory, address, room):
        self.directory = directory
        self.address = address
        self.room = room
        self.request = directory / (address + '.request')
        self.selected = directory / 'selected-address'
        self.ack = directory / (address + '.ack')
        self.last = None
        self.sock = None
        self.next_check = 0

    def trace(self, event, peer=None):
        # Diagnostics contain addresses and timestamps, never the room token.
        try:
            atomic(self.directory / (self.address + '.discovery.json'),
                   dict(event=event, address=self.address, pid=os.getpid(),
                        peer=peer, time=time.time()))
        except OSError:
            pass

    def close(self):
        if self.sock is not None:
            self.sock.close()
            self.sock = None

    def tick(self):
        now = time.monotonic()
        if now >= self.next_check:
            self.next_check = now + 0.25
            try:
                # Only the currently selected hotspot may own discovery.
                # Keep it available for this room's lifetime: loading the game
                # must not consume a guest's chance to find the room.
                if self.selected.exists() and self.selected.read_text(encoding='ascii').strip() != self.address:
                    self.close()
                    return
                if 0 <= time.time() - self.request.stat().st_mtime < 45:
                    data = self.request.read_bytes()[:128]
                    match = re.fullmatch(rb'([0-9a-f]{32}) ([0-9]{1,2})', data)
                    if match and data != self.last:
                        self.open(match, data, now)
            except (OSError, ValueError):
                pass
        if self.sock is not None:
            for _ in range(12):
                try:
                    packet, peer = self.sock.recvfrom(128)
                    if ipaddress.ip_address(peer[0]) in self.network and re.fullmatch(rb'SGC_FIND [0-9a-f]{32}', packet):
                        self.sock.sendto(b'SGC_ROOM ' + packet[9:] + b' ' + self.room[1:].encode('ascii'), peer)
                        self.trace('replied', peer)
                except OSError:
                    break

    def open(self, match, data, now):
        self.network = ipaddress.IPv4Interface(f'{self.address}/{int(match[2])}').network
        if ipaddress.ip_address(self.address) in ipaddress.ip_network('100.64.0.0/10'):
            self.network = ipaddress.ip_network('100.64.0.0/10')
        if not any(self.network.subnet_of(net) for net in PRIVATE_NETWORKS):
            return
        if self.sock is None:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
                    sock.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                sock.bind(('', 38478))
                sock.setblocking(False)
            except OSError:
                sock.close()
                raise
            self.sock = sock
        digest = hashlib.sha256(self.room[1:].encode('ascii')).hexdigest()
        temporary = self.ack.with_suffix('.ack-new')
        temporary.write_text(f'{match[1].decode()} {os.getpid()} {digest} READY13', encoding='ascii')
        temporary.replace(self.ack)
        self.last = data
        self.trace('listening')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--new-room', action='store_true', help='更换本机热点房间链接，使旧链接失效')
    parser.add_argument("--bind", help="本机局域网 IPv4 地址")
    parser.add_argument("--room-directory", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--check-room", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--owner-pid", type=int, help=argparse.SUPPRESS)
    parser.add_argument('--tunnel', action='store_true', help='本机回环监听，并填写穿透入口生成客机网址')
    parser.add_argument('--public-endpoint', help='穿透入口，如 http://节点域名:远程端口')
    args = parser.parse_args()
    lifetime = None
    if args.owner_pid is not None:
        from gateway_lifetime import LauncherLifetime
        lifetime = LauncherLifetime(args.owner_pid)
    if args.check_room:
        if not args.bind or not args.room_directory or not private_ipv4(args.bind):
            raise SystemExit(1)
        connection = HTTPConnection(args.bind, 38477, timeout=3)
        try:
            token = (args.room_directory / (args.bind + '.room')).read_text(encoding='ascii').strip()
            if not re.fullmatch(r'[A-Za-z0-9_-]{24}', token):
                raise SystemExit(1)
            connection.request('GET', '/' + token + '/health')
            response = connection.getresponse()
            expected = release_health()
            raise SystemExit(0 if response.status == 200 and expected.startswith('COOP_ROOM_084 ') and response.read(128).decode('ascii') == expected else 1)
        except OSError:
            raise SystemExit(1)
        finally:
            connection.close()
    addr = args.bind
    endpoint = None
    if args.tunnel or args.public_endpoint:
        if addr not in (None, '127.0.0.1'):
            parser.error('穿透模式固定监听 127.0.0.1，请把隧道本地目标设为 127.0.0.1:38477')
        addr = '127.0.0.1'
        candidate = args.public_endpoint
        while endpoint is None:
            if candidate is None:
                candidate = input('请输入穿透入口（http://域名:端口；自动HTTPS请填 https://域名:端口）：')
            try:
                endpoint = public_endpoint(candidate)
            except ValueError as error:
                if args.public_endpoint:
                    parser.error(str(error))
                print(str(error))
                candidate = None
    if not addr:
        addresses = sorted({ip for ip in socket.gethostbyname_ex(socket.gethostname())[2]
                            if private_ipv4(ip)})
        if len(addresses) == 1:
            addr = addresses[0]
        else:
            print("检测到的局域网地址：" + "、".join(addresses))
            addr = input("请输入与客机同网段的本机 IPv4 地址：").strip()
    if addr != "127.0.0.1" and not private_ipv4(addr):
        raise SystemExit("请选择本机的局域网 IPv4 地址。")
    with RoomServer((addr, 38477), Remote) as server:
        server.save_store = SaveStore(Path(__file__).resolve().parent / 'room-control' / 'session-share')
        if endpoint:
            server.room = "/" + secrets.token_urlsafe(18)
        else:
            directory = args.room_directory or Path(os.environ.get('LOCALAPPDATA') or Path.home()) / 'ShadowGambitCoop' / 'rooms'
            server.room = lan_room(directory, addr, args.new_room)
        server.session_broker = SessionBroker(Path(__file__).resolve().parent / "session-next/runtime/control", server.save_store)
        server.session_broker.start()
        server.game_port = 38476
        server.pairing = RoomPairing(Path(__file__).resolve().parent / 'session-next/runtime/steam-launch/host-invite.json', server.room)
        if endpoint:
            print(f'本机检查网址：http://127.0.0.1:38477{server.room}', flush=True)
            print(f'发给客机的完整网址：{endpoint}{server.room}', flush=True)
            print('请保持穿透隧道运行。本程序只生成连接网址，不会替你建立樱花隧道。', flush=True)
        else:
            print(f"请同一局域网的队友用浏览器打开 http://{addr}:38477{server.room}", flush=True)
        print("保持本窗口打开。按 Ctrl+C 结束本次测试。", flush=True)
        local = frames = tunnel_local = tunnel_frames = native_tcp = None
        unified_frames = []
        try:
            if not endpoint and addr != '127.0.0.1':
                local = RoomServer(('127.0.0.1', 0), HostHealth)
                local.live = server
                local.room_file = directory / (addr + '.room')
                local.repair_lock = threading.Lock()
                threading.Thread(target=local.serve_forever, daemon=True).start()
            if not endpoint and addr != '127.0.0.1':
                pairing = PairingWindow(Path(__file__).resolve().parent / 'room-control', addr, server.room)
                server.service_actions = pairing.tick
                try:
                    frames = LanFrames(addr, server.server_port, server.room, server.game_port)
                except OSError:
                    print('UDP 快照端口不可用；继续使用 HTTP 同步。', flush=True)
            # Stable loopback targets for frpc, independent of hotspot changes.
            # Reuse the exact room, broker and token checks of the LAN listener.
            try:
                if addr != '127.0.0.1':
                    tunnel_local = RoomServer(('127.0.0.1', 38477), Remote)
                    tunnel_local.room = server.room
                    tunnel_local.save_store = server.save_store
                    tunnel_local.session_broker = server.session_broker
                    tunnel_local.game_port = server.game_port
                    tunnel_local.pairing = server.pairing
                    threading.Thread(target=tunnel_local.serve_forever, daemon=True).start()
                tunnel_frames = LanFrames('127.0.0.1', 38477, server.room, server.game_port, fixture=True)
            except OSError:
                print('穿透回环端口不可用，请检查 127.0.0.1:38477 的占用。', flush=True)
            # UDP and TCP have separate port spaces. Keep UDP 38477 alive for
            # older clients, while new tunnels share local port 38479.
            for udp_address in dict.fromkeys(('127.0.0.1', addr)):
                try:
                    unified_frames.append(LanFrames(udp_address, 38479, server.room, server.game_port, fixture=(udp_address == '127.0.0.1')))
                except OSError as error:
                    print('UDP 38479 unavailable; TCP remains available: '+str(error), flush=True)
            try:
                if addr != '127.0.0.1' and tunnel_local is None:
                    raise OSError('Local room endpoint is unavailable')
                native_tcp = NativeServer(('127.0.0.1',38479), ('127.0.0.1',38477), server.room)
                threading.Thread(target=native_tcp.serve_forever,daemon=True).start()
                print('独立 TCP 入口：127.0.0.1:38479 (SGCTCP1)',flush=True)
            except OSError as error:
                print('独立 TCP 入口未启动：'+str(error),flush=True)
            if lifetime is not None:
                lifetime.watch(server.shutdown)
            server.serve_forever(poll_interval=0.08)
        except KeyboardInterrupt:
            pass
        finally:
            for feed in unified_frames:
                feed.close()
            if native_tcp is not None:
                native_tcp.shutdown()
                native_tcp.server_close()
            if tunnel_frames is not None:
                tunnel_frames.close()
            if tunnel_local is not None:
                tunnel_local.shutdown()
                tunnel_local.server_close()
            server.session_broker.close()
            if frames is not None:
                frames.close()
            if local is not None:
                local.server_close()


if __name__ == "__main__":
    main()
