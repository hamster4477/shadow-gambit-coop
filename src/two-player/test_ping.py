"""Ping validation plus real local HTTP / SGCTCP1 transport (not two-game tests)."""
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import socket
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from control_file import atomic
from lobby_broker import serve_lobby
from native_tcp import NativeServer, send, receive
from ping_broker import PingBroker, valid


def frame(**changes):
    value = dict(operation='ping', world='a'*32, client='b'*32, revision=1,
                 marks=[dict(id='c'*32, x=1.25, y=2, z=-3, ageMs=50)])
    value.update(changes)
    return value


class PingTests(unittest.TestCase):
    def setUp(self):
        # test-results is git-ignored, so a fresh checkout does not have it.
        results = Path(__file__).parent / 'test-results'
        results.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=results)
        self.control = Path(self.temp.name)
        self.host = self.control / 'host-ping.json'
        atomic(self.host, frame(client='d'*32))
        self.broker = PingBroker(self.control)

    def tearDown(self):
        self.temp.cleanup()

    def test_valid_and_bounds(self):
        self.assertTrue(valid(frame()))
        for key, bad in [('world', 'bad'), ('client', 'z'*32), ('revision', 0), ('revision', True),
                         ('revision', 2**63), ('operation', 'move'), ('marks', None),
                         ('marks', frame()['marks']*9)]:
            with self.subTest(key=key, bad=bad):
                self.assertFalse(valid(frame(**{key: bad})))
        invalid = frame(); invalid['unknown'] = 1
        self.assertFalse(valid(invalid))
        self.assertFalse(valid([]))
        self.assertFalse(valid(frame(marks=frame()['marks']*2)))
        second=frame()['marks'][0];second['id']='e'*32
        self.assertFalse(valid(frame(marks=frame()['marks']+[second])))
        for key, bad in [('id', 'bad'), ('ageMs', -1), ('ageMs', 5001), ('ageMs', True),
                         ('x', float('nan')), ('y', float('inf')), ('z', 1000001),
                         ('x', True), ('x', '1')]:
            invalid = frame(); invalid['marks'][0][key] = bad
            with self.subTest(key=key, bad=bad):
                self.assertFalse(valid(invalid))

    def test_mailbox_retry_and_revision(self):
        result = self.broker.exchange(frame())
        self.assertEqual(result['client'], 'd'*32)
        path = self.control/'guest-ping.json'
        self.assertEqual(json.loads(path.read_text()), frame())
        stamp = path.stat().st_mtime_ns
        self.broker.exchange(frame())
        self.assertEqual(path.stat().st_mtime_ns, stamp)
        conflicting = frame(); conflicting['marks'][0]['x'] = 7
        with self.assertRaises(ValueError): self.broker.exchange(conflicting)
        self.broker.exchange(frame(revision=2))
        with self.assertRaises(ValueError): self.broker.exchange(frame())

    def test_host_scene_freshness_and_age(self):
        with self.assertRaises(ValueError): self.broker.exchange(frame(world='e'*32))
        age = .6
        stamp = self.host.stat().st_mtime
        os.utime(self.host, (stamp-age, stamp-age))
        result = self.broker.exchange(frame())
        self.assertGreaterEqual(result['marks'][0]['ageMs'], 600)
        self.assertLess(result['marks'][0]['ageMs'], 1000)
        os.utime(self.host, (1, 1))
        with self.assertRaises(ValueError): self.broker.exchange(frame())

    def test_invalid_host_never_writes_guest(self):
        for invalid in [frame(world='bad'), {'operation': 'ping'}, ['wrong type']]:
            self.host.write_text(json.dumps(invalid))
            with self.assertRaises(ValueError): self.broker.exchange(frame())
            self.assertFalse((self.control/'guest-ping.json').exists())

    def test_immediate_replacement_and_nonce_cache(self):
        with patch('ping_broker.time.monotonic', return_value=100):
            self.broker.exchange(frame())
            newer=frame(revision=2);newer['marks'][0]['id']='e'*32;newer['marks'][0]['x']=99
            self.broker.exchange(newer)  # No cooldown: newer pings replace the previous one.
            self.assertEqual(json.loads((self.control/'guest-ping.json').read_text()),newer)
            self.broker.exchange(newer)  # Identical network retry is harmless.
        for number in range(1, 8):
            with patch('ping_broker.time.monotonic', return_value=100.5):
                self.broker.exchange(frame(client=f'{number:032x}'))
        with patch('ping_broker.time.monotonic', return_value=101):
            with self.assertRaises(ValueError): self.broker.exchange(frame(client='f'*32))
        self.assertEqual(len(self.broker.clients), 8)

    def test_host_nonce_resets_revision_state(self):
        self.broker.exchange(frame(revision=20))
        atomic(self.host, frame(client='f'*32))
        self.broker.exchange(frame())


class HTTPHandler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *args): pass
    def body(self, limit):
        length = int(self.headers.get('Content-Length', '0'))
        if not 0 < length <= limit: raise ValueError('Body too large')
        return self.rfile.read(length)
    def plain(self, value):
        data = value.encode('ascii')
        self.send_response(200)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(data)
    def do_POST(self):
        if not serve_lobby(self): self.send_error(404)
    def do_GET(self):
        if not serve_lobby(self): self.send_error(404)


class PingTransportTests(unittest.TestCase):
    def setUp(self):
        PingTests.setUp(self)
        self.http = ThreadingHTTPServer(('127.0.0.1', 0), HTTPHandler)
        self.http.daemon_threads = True
        self.http.room = '/room'
        self.http.session_broker = SimpleNamespace(control=self.control, lock=threading.Lock())
        self.http_thread = threading.Thread(target=self.http.serve_forever, daemon=True)
        self.http_thread.start()
        self.tcp = NativeServer(('127.0.0.1', 0), self.http.server_address, '/room')
        self.tcp_thread = threading.Thread(target=self.tcp.serve_forever, daemon=True)
        self.tcp_thread.start()

    def tearDown(self):
        self.tcp.shutdown(); self.tcp.server_close(); self.tcp_thread.join(2)
        self.http.shutdown(); self.http.server_close(); self.http_thread.join(2)
        PingTests.tearDown(self)

    def test_http_and_native_tcp_reuse(self):
        conn = http.client.HTTPConnection(*self.http.server_address, timeout=3)
        try:
            for _ in range(2):
                conn.request('POST', '/room/lobby', body=json.dumps(frame()),
                             headers={'X-Coop-Lobby':'1', 'Content-Type':'application/json'})
                response = conn.getresponse(); data = response.read()
                self.assertEqual(response.status, 200)
                self.assertEqual(json.loads(data)['marks'][0]['x'], 1.25)
        finally:
            conn.close()
        with socket.create_connection(self.tcp.server_address, timeout=3) as stream:
            for _ in range(3):
                send(stream, dict(op='request', method='POST', path='/room/lobby',
                                  headers={'X-Coop-Lobby':'1', 'Content-Type':'application/json'}),
                     json.dumps(frame()).encode('ascii'))
                header, raw = receive(stream)
                self.assertEqual(header['status'], 200)
                self.assertEqual(json.loads(raw)['world'], 'a'*32)

    def test_http_rejects_browser_and_missing_header(self):
        for headers in ({}, {'X-Coop-Lobby':'1', 'Origin':'https://example.invalid'}):
            conn = http.client.HTTPConnection(*self.http.server_address, timeout=3)
            try:
                conn.request('POST', '/room/lobby', body=json.dumps(frame()), headers=headers)
                response = conn.getresponse(); response.read()
                self.assertEqual(response.status, 409)
            finally:
                conn.close()
        self.assertFalse((self.control/'guest-ping.json').exists())


if __name__ == '__main__':
    unittest.main()
