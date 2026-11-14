"""Real loopback gateway/SGCTCP1/UDP paths with a peer-aware fake game.

No installed game, save, runtime directory, public network or fixed port is used.
"""
from concurrent.futures import ThreadPoolExecutor
import hashlib
from http.client import HTTPConnection
import json
from pathlib import Path
import socket
import sys
import tempfile
import threading
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from lan_remote import Remote, RoomServer, LanFrames
from native_tcp import NativeServer, send, receive
from session_broker import SessionBroker
from save_transfer import SaveStore


def identity(number):
    return format(number, '032x')


def ticket(peer):
    return hashlib.sha256(peer.encode()).hexdigest()[:32]


class FakeGame:
    def __init__(self):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.bind(('127.0.0.1', 0))
        self.socket.settimeout(.05)
        self.port = self.socket.getsockname()[1]
        self.stop = threading.Event()
        self.messages = []
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        while not self.stop.is_set():
            try:
                raw, address = self.socket.recvfrom(65536)
            except TimeoutError:
                continue
            self.messages.append(raw.decode())
            parts = raw.decode().split(' ', 2)
            if len(parts) != 3 or parts[0] != 'PEER':
                reply = 'MISSING_PEER'
            else:
                _, peer, command = parts
                if command == 'CONTROL':
                    reply = 'CONTROL ' + ticket(peer)
                elif command.startswith('STATE'):
                    reply = 'STATE ' + peer + ' ' + command[5:].strip() + ' ' + 'x' * 2300
                elif command.startswith(('POINT ', 'ORDER ')):
                    used_ticket = command.split(' ')[1]
                    reply = ('ACCEPTED ' if used_ticket == ticket(peer) else 'REJECTED ') + peer
                else:
                    reply = 'UNEXPECTED ' + peer
            self.socket.sendto(reply.encode(), address)

    def close(self):
        self.stop.set()
        self.thread.join(1)
        self.socket.close()


class MultiplayerTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.game = FakeGame()
        self.server = RoomServer(('127.0.0.1', 0), Remote)
        self.server.daemon_threads = True
        self.server.room = '/' + 't' * 24
        self.server.game_port = self.game.port
        self.server.session_broker = SessionBroker(self.root / 'control', SaveStore(self.root / 'share'))
        self.server_thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.server_thread.start()
        self.native = NativeServer(('127.0.0.1', 0), self.server.server_address, self.server.room)
        self.native_thread = threading.Thread(target=self.native.serve_forever, daemon=True)
        self.native_thread.start()
        self.frames = LanFrames('127.0.0.1', 0, self.server.room, self.game.port, fixture=True)

    def tearDown(self):
        self.frames.close()
        self.native.shutdown()
        self.native.server_close()
        self.native_thread.join(2)
        self.server.shutdown()
        self.server.server_close()
        self.server_thread.join(2)
        self.game.close()
        self.temp.cleanup()

    def headers(self, peer, **other):
        return {'X-Coop-Peer': peer, **other}

    def payload(self, peer, sequence=1, *, other_peer=None):
        return f'{ticket(other_peer or peer)} {"C" * 64} {"d" * 32} GW 1 2 3 Q 1 {sequence}'

    def http_request(self, connection, peer, method, suffix, body=None, headers=None):
        connection.request(method, self.server.room + suffix, body, self.headers(peer, **(headers or {})))
        response = connection.getresponse()
        data = response.read()
        self.assertEqual(response.status, 200, data)
        return data.decode()

    def native_request(self, channel, peer, method, suffix, body=None, headers=None):
        send(channel, dict(op='request', method=method, path=self.server.room + suffix,
                           headers=self.headers(peer, **(headers or {}))), (body or '').encode())
        reply, data = receive(channel)
        self.assertEqual(reply['status'], 200, data)
        return data.decode()

    def test_http_3_12_32_peers_preserve_identity_and_command_ticket(self):
        for count in (3, 12, 32):
            with self.subTest(peers=count):
                # Establish sequentially so connection backlog is not the subject
                # of this command/state isolation test; requests run concurrently.
                connections = [HTTPConnection(*self.server.server_address, timeout=10) for _ in range(count)]
                try:
                    for index, connection in enumerate(connections, 1):
                        self.assertEqual(self.http_request(connection, identity(index), 'GET', '/point'), ticket(identity(index)))
                    barrier = threading.Barrier(count)
                    def client(index):
                        peer = identity(index + 1)
                        connection = connections[index]
                        barrier.wait(10)
                        for serial in range(1, 5):
                            seen = identity(500) + f' {serial} R'
                            state = self.http_request(connection, peer, 'GET', '/state', headers={'X-Coop-Seen': seen})
                            self.assertTrue(state.startswith('STATE ' + peer + ' ' + seen + ' '), state[:100])
                            accepted = self.http_request(connection, peer, 'POST', '/point', self.payload(peer, serial), {'X-Coop-Next': '1'})
                            self.assertEqual(accepted, 'ACCEPTED ' + peer)
                        denied = self.http_request(connection, peer, 'POST', '/point', self.payload(peer, other_peer=identity(99)))
                        self.assertEqual(denied, 'REJECTED ' + peer)
                    with ThreadPoolExecutor(max_workers=count) as executor:
                        list(executor.map(client, range(count)))
                finally:
                    for connection in connections:
                        connection.close()
                self.assertTrue(all(identity(i) in self.server.session_broker.peer_seen for i in range(1, count + 1)))

    def test_native_3_12_32_peers_with_three_independent_channels_each(self):
        maximum_channels = 0
        for count in (3, 12, 32):
            with self.subTest(peers=count):
                channels = []
                try:
                    for index in range(count):
                        lanes = []
                        channels.append(lanes)
                        for _ in range(3):
                            channel = socket.create_connection(self.native.server_address, timeout=10)
                            lanes.append(channel)
                            send(channel, dict(op='hello'))
                            header, body = receive(channel)
                            self.assertEqual(header.get('room'), self.server.room)
                            self.assertEqual(body, b'')
                    maximum_channels = max(maximum_channels, sum(map(len, channels)))
                    barrier = threading.Barrier(count)
                    def client(index):
                        peer = identity(index + 1)
                        control, state, commands = channels[index]
                        barrier.wait(10)
                        self.assertEqual(self.native_request(control, peer, 'GET', '/point'), ticket(peer))
                        for serial in range(1, 5):
                            seen = identity(600) + f' {serial} R'
                            body = self.native_request(state, peer, 'GET', '/state', headers={'X-Coop-Seen': seen})
                            self.assertTrue(body.startswith('STATE ' + peer + ' ' + seen + ' '), body[:100])
                            result = self.native_request(commands, peer, 'POST', '/point', self.payload(peer, serial), {'X-Coop-Next': '1'})
                            self.assertEqual(result, 'ACCEPTED ' + peer)
                        denied = self.native_request(commands, peer, 'POST', '/point', self.payload(peer, other_peer=identity(99)))
                        self.assertEqual(denied, 'REJECTED ' + peer)
                    with ThreadPoolExecutor(max_workers=count) as executor:
                        list(executor.map(client, range(count)))
                finally:
                    for lanes in channels:
                        for channel in lanes:
                            channel.close()
        self.assertEqual(maximum_channels, 96)

    def udp_frame(self, channel, peer, serial):
        fragments = {}
        first = None
        while True:
            packet = channel.recv(4096)
            fields = packet.split(b' ', 7)
            self.assertEqual(len(fields), 8, packet)
            self.assertEqual(fields[:3], [b'PART', peer.encode(), str(serial).encode()])
            header = fields[:2] + fields[4:7]
            if first is None:
                first = header
            self.assertEqual(header, first)
            fragments[int(fields[3])] = fields[7]
            count = int(fields[4])
            if len(fragments) == count:
                body = b''.join(fragments[i] for i in range(count))
                self.assertEqual(len(body), int(fields[5]))
                self.assertEqual(hashlib.sha256(body).hexdigest().encode(), fields[6])
                return body.decode()

    def test_udp_3_12_32_peers_all_receive_own_frames_without_starvation(self):
        for count in (3, 12, 32):
            with self.subTest(peers=count):
                channels = []
                cookies = []
                token = self.server.room[1:]
                try:
                    for index in range(count):
                        peer = identity(index + 1)
                        channel = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                        channel.settimeout(8)
                        channel.connect(self.frames.sock.getsockname())
                        channels.append(channel)
                        channel.send(f'HELLO {token} {peer}'.encode())
                        kind, returned_peer, cookie = channel.recv(1024).split(b' ')
                        self.assertEqual((kind, returned_peer), (b'COOKIE', peer.encode()))
                        cookies.append(cookie.decode())
                    # All streams are queued together, rather than waiting for
                    # each response before the next peer requests its frame.
                    for serial in range(1, 7):
                        for index, channel in enumerate(channels):
                            peer = identity(index + 1)
                            channel.send(f'FRAME2 {token} {peer} {cookies[index]} {serial} {identity(700)} {serial} R'.encode())
                        for index, channel in enumerate(channels):
                            peer = identity(index + 1)
                            body = self.udp_frame(channel, peer, serial)
                            self.assertTrue(body.startswith(f'STATE {peer} {identity(700)} {serial} R '), body[:100])
                finally:
                    for channel in channels:
                        channel.close()

    def test_wrong_room_missing_peer_and_cookie_do_not_reach_game(self):
        connection = HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.request('GET', '/wrong/state', headers=self.headers(identity(1)))
            response = connection.getresponse()
            self.assertEqual(response.status, 404)
            response.read()
            connection.request('GET', self.server.room + '/state')
            response = connection.getresponse()
            self.assertEqual(response.read(), b'MULTIPLAYER_ID_REQUIRED')
        finally:
            connection.close()
        with socket.create_connection(self.native.server_address, timeout=5) as channel:
            send(channel, dict(op='request', method='GET', path='/wrong/state', headers=self.headers(identity(1))))
            response, body = receive(channel)
            self.assertEqual(response['status'], 403)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as channel:
            channel.settimeout(.2)
            channel.connect(self.frames.sock.getsockname())
            token = self.server.room[1:]
            channel.send(f'FRAME2 {token} {identity(1)} {"0" * 32} 1'.encode())
            with self.assertRaises(TimeoutError):
                channel.recv(1024)
        self.assertEqual(self.game.messages, [])


if __name__ == '__main__':
    unittest.main()
