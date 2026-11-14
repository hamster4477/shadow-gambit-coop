"""Room preparation state and bounded native selection mailbox, without file paths on the wire."""
import json
import math
import re
import threading
import time
from session_broker import atomic
from control_file import read_json
from ping_broker import exchange as exchange_pings

MAX_BYTES = 32768


def read_fresh(path, maximum=MAX_BYTES, age=5):
    return read_json(path, maximum, age)


def request_valid(value):
    return (isinstance(value, dict) and set(value) == {'request', 'room', 'revision', 'character', 'selected'}
            and all(isinstance(value[k], str) and re.fullmatch('[0-9a-f]{32}', value[k]) for k in ('request', 'room'))
            and isinstance(value['character'], str) and re.fullmatch('[0-9A-F]{64}', value['character'])
            and type(value['revision']) is int and 0 < value['revision'] < 2**63
            and type(value['selected']) is bool)


class LobbyBroker:
    def __init__(self, control):
        self.control = control
        self.lock = threading.Lock()

    def state(self):
        value = read_fresh(self.control / 'host-lobby.json')
        if value.get('schema') != 1 or not isinstance(value.get('room'), str) or not re.fullmatch('[0-9a-f]{32}', value['room']):
            raise ValueError('Invalid preparation state')
        # Explicit public fields: never forward incidental native diagnostics.
        keys = ('schema', 'room', 'revision', 'phase', 'mission', 'title', 'limit', 'ready', 'characters')
        result = {key: value[key] for key in keys}
        description = value.get('description', '')
        if not isinstance(description, str) or len(description) > 2048 or any(ord(c) < 32 for c in description):
            raise ValueError('Invalid mission description')
        result['description'] = description
        speed = value.get('gameSpeed')
        if speed is not None and (type(speed) not in (int, float) or not math.isfinite(speed) or not .01 <= speed <= 8):
            raise ValueError('Invalid game speed')
        result['gameSpeed'] = speed
        effect = value.get('slowEffect')
        if effect is not None and type(effect) is not bool:
            raise ValueError('Invalid slow motion effect')
        result['slowEffect'] = effect
        remaining = value.get('alarmRemaining')
        if remaining is not None and (type(remaining) not in (float, int) or not math.isfinite(remaining) or not 0 <= remaining <= 86400):
            raise ValueError('Invalid alarm duration')
        result['alarmRemaining'] = remaining
        for key in ('shipHost', 'shipGuest'):
            actor = value.get(key)
            if actor is not None and (not isinstance(actor, str) or not re.fullmatch('[0-9A-F]{64}', actor)):
                raise ValueError('Invalid ship actor')
            result[key] = actor
        world = value.get('world')
        if world is not None and (not isinstance(world, str) or not re.fullmatch('[0-9a-f]{32}', world)):
            raise ValueError('Invalid scene identity')
        stage = value.get('nativeStage')
        spawn = value.get('spawn')
        departure = value.get('departure', [])
        if stage is not None and (not isinstance(stage, str) or not re.fullmatch('[A-Za-z0-9_]{1,64}', stage)):
            raise ValueError('Invalid native preparation stage')
        if spawn is not None and (not isinstance(spawn, str) or not re.fullmatch('[0-9A-F]{64}', spawn)):
            raise ValueError('Invalid landing point')
        if (not isinstance(departure, list) or len(departure) > 8 or
                any(not isinstance(c, str) or not re.fullmatch('[0-9A-F]{64}', c) for c in departure) or len(set(departure)) != len(departure)):
            raise ValueError('Invalid departure crew')
        result.update(nativeStage=stage, spawn=spawn, departure=departure)
        result['world'] = world
        return result

    def select(self, request):
        if not request_valid(request):
            raise ValueError('Invalid preparation request')
        if not self.lock.acquire(timeout=0.05):
            raise ValueError('Selection request in progress')
        try:
            frame = self.state()
            if frame['room'] != request['room']:
                raise ValueError('Preparation changed')
            command = self.control / 'host-lobby-command.json'
            reply_path = self.control / 'host-lobby-reply.json'
            if command.exists():
                try:
                    old = read_fresh(command, 2048, 3)
                except (OSError, ValueError):
                    old = None
                if old:
                    try:
                        reply = read_fresh(reply_path, 2048, 3)
                    except (OSError, ValueError):
                        reply = None
                    if old['request'] == request['request']:
                        if old != request:
                            raise ValueError('Request identity changed')
                        if reply and reply.get('request') == request['request']:
                            return reply
                    elif not reply or reply.get('request') != old['request']:
                        raise ValueError('Earlier native request still pending')
            ship = frame['mission'] == -379511569 and frame.get('shipHost') is not None and frame['phase'] in ('playing', 'paused')
            if frame['revision'] != request['revision'] or (frame['phase'] != 'crew' and not ship) or not frame['ready']:
                raise ValueError('Preparation changed')
            atomic(command, request)
            until = time.monotonic() + 2.5
            while time.monotonic() < until:
                try:
                    reply = read_fresh(reply_path, 2048, 3)
                    if reply.get('request') == request['request']:
                        return {key: reply[key] for key in ('request', 'accepted', 'reason')}
                except (OSError, ValueError):
                    pass
                time.sleep(0.025)
            raise ValueError('Native selection response timed out')
        finally:
            self.lock.release()


def training_status(control, payload):
    # A room-scoped mailbox; no file paths or scene IDs accepted from clients.
    if not isinstance(payload, dict) or set(payload) != {'operation', 'active'} or payload['operation'] != 'training' or type(payload['active']) is not bool:
        raise ValueError('Invalid tutorial presence')
    atomic(control / 'training-guest.json', dict(schema=1, active=payload['active']))
    try:
        host = read_fresh(control / 'training-host.json', 1024, 15)
        if host.get('schema') != 1 or type(host.get('active')) is not bool:
            raise ValueError('Invalid tutorial host presence')
    except (OSError, ValueError):
        # Do not tell the guest it may rejoin while the host is unavailable.
        return dict(accepted=True, host=None)
    return dict(accepted=True, host=host['active'])


def planning_valid(value):
    if not isinstance(value, dict) or set(value) != {'operation','client','world','revision','active','queues','request','barrier','executed','requestActor','executedActor'}:
        return False
    if value['operation'] != 'planning' or not isinstance(value['client'],str) or not re.fullmatch('[a-f0-9]{32}',value['client']) or not isinstance(value['world'], str) or not re.fullmatch('[a-f0-9]{32}', value['world']) or type(value['revision']) is not int or not 0 < value['revision'] < 2**63 or type(value['active']) is not bool:
        return False
    if any(v is not None and (not isinstance(v,str) or not re.fullmatch('[a-f0-9]{32}',v)) for v in (value['request'],value['barrier'],value['executed'])):
        return False
    if any(value[k] is not None and (not isinstance(value[k],str) or not re.fullmatch('[A-F0-9]{64}',value[k])) for k in ('requestActor','executedActor')):return False
    queues=value['queues']
    if not isinstance(queues,list) or len(queues)>16:return False
    def vector(v):return isinstance(v,list) and len(v)==3 and all(type(n) in (int,float) and math.isfinite(n) and abs(n)<=100000 for n in v)
    def command(c):
        if not isinstance(c,dict) or set(c)-{'transformEntity'}!={'skill','flags','target','entity','transform','point','skillPoint','offset','alternate','movement','stopping','modifier','update','data','value','type','origin','dataPoint'}:return False
        if not isinstance(c['skill'],str) or not re.fullmatch('[A-Za-z0-9_]{1,80}',c['skill']):return False
        if any(type(c[k]) is not int or not -(2**31)<=c[k]<2**31 for k in ('flags','movement','modifier','type')):return False
        if any(type(c[k]) is not bool for k in ('target','update','data','value')):return False
        if any(c[k] is not None and (not isinstance(c[k],str) or not re.fullmatch('[A-Za-z0-9-]{1,160}',c[k])) for k in ('entity','origin')):return False
        if c['transform'] is not None and (not isinstance(c['transform'],str) or not re.fullmatch('[A-F0-9]{64}',c['transform'])):return False
        if c.get('transformEntity') is not None and (not isinstance(c['transformEntity'],str) or not re.fullmatch('[A-Za-z0-9-]{1,160}',c['transformEntity'])):return False
        if type(c['stopping']) not in (int,float) or not math.isfinite(c['stopping']) or abs(c['stopping'])>100000:return False
        if c['target'] and not all(vector(c[k]) for k in ('point','skillPoint','offset','alternate')):return False
        return not c['data'] or c['target'] and vector(c['dataPoint'])
    ids=set();count=0
    for q in queues:
        if not isinstance(q,dict) or set(q)!={'actor','steps'} or not isinstance(q['actor'],str) or not re.fullmatch('[A-F0-9]{64}',q['actor']) or q['actor'] in ids or not isinstance(q['steps'],list) or len(q['steps'])>16:return False
        ids.add(q['actor'])
        for step in q['steps']:
            if not isinstance(step,dict) or set(step)!={'command','chain','crouch','execute','variant','mouse','cancel','controller','executionTarget'} or type(step['crouch']) is not bool or type(step['execute']) is not bool or any(type(step[k]) is not int or not -(2**31)<=step[k]<2**31 for k in ('variant','mouse','cancel','controller')) or step['executionTarget'] is not None and not vector(step['executionTarget']) or not command(step['command']) or not isinstance(step['chain'],list) or len(step['chain'])>8 or not all(command(c) for c in step['chain']):return False
            count+=1+len(step['chain'])
            if count>32:return False
    return len(json.dumps(value,separators=(',',':'),ensure_ascii=True))<=30000


def planning_status(control,payload):
    if not planning_valid(payload):raise ValueError('Invalid planning queues')
    host=read_fresh(control/'host-planning.json',32768,3)
    if not planning_valid(host) or host['world']!=payload['world']:raise ValueError('Planning scene changed')
    atomic(control/'guest-planning.json',payload)
    return host


def serve_lobby(handler):
    if handler.path == handler.server.room + '/story':
        return serve_story(handler)
    if handler.path != handler.server.room + '/lobby':
        return False
    session = getattr(handler.server, 'session_broker', None)
    if session is None:
        handler.send_error(404)
        return True
    try:
        with session.lock:
            if not hasattr(session, 'lobby'):
                session.lobby = LobbyBroker(session.control)
            broker = session.lobby
        if handler.command == 'GET':
            if not handler.headers.get('Origin'):
                atomic(session.control / 'lobby-peer.json', dict(schema=1))
            result = session.reload_lobby()
            if result:
                # Keep the native departure plan while the faster host waits
                # at its checkpoint. Never combine different scene epochs.
                try:
                    native = broker.state()
                    if native.get('world') == result.get('world') and native.get('mission') == result.get('mission'):
                        for key in ('nativeStage', 'spawn', 'departure', 'gameSpeed', 'slowEffect', 'alarmRemaining'):
                            result[key] = native[key]
                except (OSError, ValueError, KeyError):
                    pass
            else:
                result = broker.state()
        elif handler.command == 'POST' and not handler.headers.get('Origin') and handler.headers.get('X-Coop-Lobby') == '1':
            payload = json.loads(handler.body(32768))
            if isinstance(payload, dict) and payload.get('operation') == 'ping':
                result = exchange_pings(session, payload)
            elif isinstance(payload, dict) and payload.get('operation') == 'planning':
                result = planning_status(session.control, payload)
            elif isinstance(payload, dict) and payload.get('operation') == 'training':
                result = training_status(session.control, payload)
            elif isinstance(payload, dict) and payload.get('operation') == 'entry-ready':
                result = entry_ready(broker, session.control, payload)
            else:
                result = broker.select(payload)
        else:
            raise ValueError('Invalid preparation operation')
        handler.plain(json.dumps(result, ensure_ascii=True))
    except (OSError, ValueError, TypeError, KeyError):
        handler.send_error(409, 'Preparation is unavailable or changed')
    return True


def serve_story(handler):
    session = getattr(handler.server, 'session_broker', None)
    if session is None:
        handler.send_error(404)
        return True
    try:
        if handler.headers.get('Origin'):
            raise ValueError('Browser story mutation is not allowed')
        frame = read_fresh(session.control / 'host-story.json', MAX_BYTES, 3)
        if (frame.get('schema') != 1 or not isinstance(frame.get('world'), str)
                or not re.fullmatch('[0-9a-f]{32}', frame['world'])
                or type(frame.get('last')) is not int or frame['last'] < 0):
            raise ValueError('Invalid story frame')
        if handler.command == 'GET':
            result = frame
        elif handler.command == 'POST' and handler.headers.get('X-Coop-Lobby') == '1':
            value = json.loads(handler.body(8192))
            if (set(value) != {'schema', 'world', 'seen', 'request', 'ready', 'playing', 'triggers'} or type(value.get('playing')) is not bool or value['schema'] != 1
                    or value['world'] != frame['world'] or type(value['seen']) is not int
                    or not 0 <= value['seen'] <= frame['last']
                    or not isinstance(value['request'], str) or not re.fullmatch('[0-9a-f]{32}', value['request'])
                    or not story_ready_valid(value['ready']) or not story_triggers_valid(value['triggers'])):
                raise ValueError('Stale or invalid story acknowledgement')
            atomic(session.control / 'guest-story.json', value)
            result = {'accepted': True}
        else:
            raise ValueError('Invalid story operation')
        handler.plain(json.dumps(result, ensure_ascii=True))
    except (OSError, ValueError, TypeError, KeyError):
        handler.send_error(409, 'Story state unavailable or changed')
    return True


def story_ready_valid(ready):
    return (isinstance(ready, list) and len(ready) <= 16
            and all(isinstance(key, str) and re.fullmatch('[0-9A-F]{64}', key) for key in ready)
            and len(set(ready)) == len(ready))


def story_triggers_valid(events):
    if not isinstance(events, list) or len(events) > 16:
        return False
    previous = None
    for e in events:
        if (not isinstance(e, dict) or set(e) - {'hubDialog', 'consumeCurrency', 'userAction'} not in ({'seq', 'after', 'kind', 'key', 'collider', 'fromCutscene', 'skipped', 'segment'}, {'seq', 'after', 'kind', 'key', 'collider', 'fromCutscene', 'skipped', 'segment', 'usable', 'actor'})
                or type(e['seq']) is not int or not 0 < e['seq'] < 2**63
                or type(e['after']) is not int or not 0 <= e['after'] < 2**63
                or previous is not None and e['seq'] != previous + 1
                or e['kind'] not in ('script', 'timeline', 'dialog', 'used', 'finished', 'hub', 'revive', 'plan-enter', 'plan-exit', 'plan-execute')
                or not isinstance(e['key'], str) or not re.fullmatch('[0-9A-F]{64}', e['key'])
                or e['collider'] is not None and (not isinstance(e['collider'], str) or not re.fullmatch('[0-9A-F]{64}', e['collider']))
                or type(e['fromCutscene']) is not bool or e['skipped'] is not False or e['segment'] is not None):
            return False
        for key in ('consumeCurrency', 'userAction'):
            if type(e.get(key, False)) is not bool or (e['kind'] != 'revive' and e.get(key, False)):
                return False
        hub_id = e.get('hubDialog', 0)
        if type(hub_id) is not int or not -(2**31) <= hub_id < 2**31 or (e['kind'] == 'hub') != (hub_id != 0):
            return False
        if e.get('usable') is None:
            if e.get('actor') is not None:
                return False
        elif any(not isinstance(e.get(k), str) or not re.fullmatch('[0-9A-F]{64}', e[k]) for k in ('usable', 'actor')):
            return False
        previous = e['seq']
    return True


def entry_ready(broker, control, payload):
    if (set(payload) != {'operation', 'world', 'mission'}
            or not isinstance(payload['world'], str)
            or not re.fullmatch('[0-9a-f]{32}', payload['world'])
            or type(payload['mission']) is not int or payload['mission'] == 0):
        raise ValueError('Invalid entry receipt')
    state = broker.state()
    if state.get('world') != payload['world'] or state.get('mission') != payload['mission']:
        raise ValueError('Entry scene changed')
    atomic(control / 'guest-entry-ready.json', dict(schema=1, world=payload['world'], mission=payload['mission']))
    return dict(accepted=True)
