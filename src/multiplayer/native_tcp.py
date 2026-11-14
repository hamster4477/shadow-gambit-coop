"""SGCTCP1 framed transport. HTTP is used only inside the host loopback.

Wire: eight-byte magic, big-endian header length, JSON header, exact body.
Connections carry repeated framed requests; independent channels never block commands
behind checkpoint downloads. No arbitrary forwarding destinations are accepted.
"""
import json
import socket
import socketserver
import struct
import time
from http.client import HTTPConnection, HTTPException

MAGIC = b'SGCTCP1\n'
MAX_BODY = 100 * 1024 * 1024
HEADERS = {'x-coop-peer', 'x-coop-session', 'x-coop-lobby', 'x-coop-seen', 'x-coop-next', 'content-type', 'origin'}
SMALL_FRAME = 65536
BACKEND_IDLE = 2.0  # Below Remote's five-second HTTP keep-alive timeout.

def exact(stream, count):
    out = bytearray()
    while len(out) < count:
        data = stream.recv(count-len(out))
        if not data: raise EOFError('Truncated native TCP frame')
        out.extend(data)
    return bytes(out)

def receive(stream, limit=MAX_BODY):
    if exact(stream, 8) != MAGIC: raise ValueError('Unsupported native TCP protocol')
    size = struct.unpack('!I', exact(stream, 4))[0]
    if not 0 < size <= 16384: raise ValueError('Header too large')
    header = json.loads(exact(stream, size))
    if not isinstance(header,dict): raise ValueError('Invalid header object')
    length = header.get('bytes')
    if type(length) is not int or not 0 <= length <= limit: raise ValueError('Invalid payload size')
    return header, exact(stream, length)

def send(stream, header, body=b''):
    header = dict(header, bytes=len(body))
    raw = json.dumps(header, separators=(',', ':')).encode('utf-8')
    prefix = MAGIC + struct.pack('!I', len(raw)) + raw
    if len(body) <= SMALL_FRAME:
        # NODELAY is enabled: separate tiny header/body writes otherwise become
        # separate packets. Avoid another whole-buffer copy for large saves.
        stream.sendall(prefix + body)
    else:
        stream.sendall(prefix)
        stream.sendall(body)

class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        self.request.settimeout(20)
        self.request.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.backend_connection = None
        self.backend_used = 0
        try:
            while self.exchange(): pass
        finally:
            if self.backend_connection is not None:
                self.backend_connection.close()

    def backend(self):
        # One backend per native channel preserves separate save, state and
        # command lanes. Do not share a connection or lock across peers.
        now = time.monotonic()
        if self.backend_connection is not None and now-self.backend_used >= BACKEND_IDLE:
            self.backend_connection.close()
            self.backend_connection = None
        if self.backend_connection is None:
            self.backend_connection = HTTPConnection(*self.server.backend, timeout=15)
        return self.backend_connection

    def exchange(self):
        try:
            header, body = receive(self.request, 65536)
            if header.get('op') == 'hello' and not body:
                send(self.request, {'status':200, 'room':self.server.room, 'protocol':1})
                return True
            path = header.get('path', '')
            if (header.get('op') != 'request' or header.get('method') not in ('GET','POST')
                    or not isinstance(path,str) or len(path)>2048 or '\r' in path or '\n' in path
                    or not path.startswith(self.server.room+'/')):
                send(self.request, {'status':403}); return
            source = header.get('headers', {})
            if not isinstance(source,dict): raise ValueError('Invalid headers')
            headers = {}
            for key, value in source.items():
                if key.lower() not in HEADERS: continue
                if not isinstance(value,str) or len(value)>4096 or '\r' in value or '\n' in value: raise ValueError('Invalid header')
                headers[key] = value
            conn = self.backend()
            conn.request(header['method'],path,body=body if header['method']=='POST' else None,headers=headers)
            response = conn.getresponse()
            length = response.getheader('Content-Length','')
            if not length.isdecimal() or int(length)>MAX_BODY: raise ValueError('Unbounded backend response')
            data = response.read(int(length)+1)
            if len(data)!=int(length): raise EOFError('Truncated backend response')
            self.backend_used = time.monotonic()
            send(self.request, {'status':response.status,'type':response.getheader('Content-Type','application/octet-stream')}, data)
            return True
        except (OSError,ValueError,EOFError,TypeError,HTTPException):
            # Never replay a POST whose delivery is uncertain. Closing this
            # channel lets the existing caller decide whether retry is safe.
            try: send(self.request, {'status':502,'transportError':True})
            except OSError: pass

class NativeServer(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = False
    # This is an accept backlog, not a limit on established player channels.
    request_queue_size = 128
    def __init__(self, address, backend, room):
        self.backend, self.room = backend, room
        super().__init__(address, Handler)
