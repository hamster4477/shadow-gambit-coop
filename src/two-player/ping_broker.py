"""Bounded scene-scoped visual pings; no game commands or save operations."""
import copy
import json
import math
import os
import re
import threading
import time

from control_file import atomic, open_shared

MAX_BYTES = 8192
MAX_AGE_MS = 5000
MAX_MARKS = 1


def token(value):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{32}', value) is not None


def valid(value):
    if not isinstance(value, dict) or set(value) != {'operation', 'world', 'client', 'revision', 'marks'}:
        return False
    if value['operation'] != 'ping' or not token(value['world']) or not token(value['client']):
        return False
    if type(value['revision']) is not int or not 0 < value['revision'] < 2**63:
        return False
    if not isinstance(value['marks'], list) or len(value['marks']) > MAX_MARKS:
        return False
    ids = set()
    for mark in value['marks']:
        if not isinstance(mark, dict) or set(mark) != {'id', 'x', 'y', 'z', 'ageMs'}:
            return False
        if not token(mark['id']) or mark['id'] in ids:
            return False
        ids.add(mark['id'])
        if type(mark['ageMs']) is not int or not 0 <= mark['ageMs'] <= MAX_AGE_MS:
            return False
        if any(type(mark[k]) not in (int, float) or not math.isfinite(mark[k]) or abs(mark[k]) > 1000000 for k in ('x', 'y', 'z')):
            return False
    return len(json.dumps(value, separators=(',', ':'), ensure_ascii=True)) <= MAX_BYTES


def read_host(path):
    # Read age and bytes from one file handle, even if Unity replaces the path.
    with open_shared(path) as stream:
        stat = os.fstat(stream.fileno())
        age = time.time() - stat.st_mtime
        if stat.st_size > MAX_BYTES or not -.001 <= age <= 3:
            raise ValueError('Ping host frame unavailable or stale')
        raw = stream.read(MAX_BYTES + 1)
    if len(raw) > MAX_BYTES:
        raise ValueError('Ping frame too large')
    value = json.loads(raw.decode('utf-8-sig'))
    if not valid(value):
        raise ValueError('Invalid host ping frame')
    return value, max(0, int(age * 1000))


class PingBroker:
    def __init__(self, control):
        self.control = control
        self.lock = threading.Lock()
        self.scene = None
        self.clients = {}

    def exchange(self, payload):
        if not valid(payload):
            raise ValueError('Invalid ping payload')
        with self.lock:
            host, age_ms = read_host(self.control / 'host-ping.json')
            if host['world'] != payload['world']:
                raise ValueError('Ping scene changed')
            scene = (host['client'], host['world'])
            if self.scene != scene:
                self.scene = scene
                self.clients.clear()
            client = payload['client']
            now = time.monotonic()
            canonical = json.dumps(payload, sort_keys=True, separators=(',', ':'), ensure_ascii=True)
            old = self.clients.get(client)
            if old is not None:
                revision, previous, _ = old
                if payload['revision'] < revision or payload['revision'] == revision and canonical != previous:
                    raise ValueError('Old or conflicting ping revision')
            duplicate = old is not None and payload['revision'] == old[0]
            if not duplicate:
                # Bounded nonce history for reconnects; do not evict live peers.
                self.clients = {key: record for key, record in self.clients.items() if now - record[2] <= 15}
                if client not in self.clients and len(self.clients) >= 8:
                    raise ValueError('Too many ping clients')
                atomic(self.control / 'guest-ping.json', payload)
                self.clients[client] = (payload['revision'], canonical, now)
            # Identical retries don't rewrite the mailbox or renew its lifetime.
            reply = copy.deepcopy(host)
            for mark in reply['marks']:
                mark['ageMs'] = min(MAX_AGE_MS, mark['ageMs'] + age_ms)
            return reply


def exchange(session, payload):
    with session.lock:
        if not hasattr(session, 'pings'):
            session.pings = PingBroker(session.control)
        broker = session.pings
    return broker.exchange(payload)
