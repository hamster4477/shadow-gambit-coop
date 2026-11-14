"""One room's native save / guest-load barrier; never accepts remote file paths."""
import json
import os
from pathlib import Path
import re
import sys
import threading
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent / 'session-next'))
from save_bundle import build
from control_file import atomic, read_json, ControlStateExpired


class SessionBroker:
    def __init__(self, control, store, *, clock=time.monotonic):
        self.control, self.store, self.clock = Path(control), store, clock
        self.lock = threading.RLock()
        self.session = None
        self.cancelled_clients = {}
        self.stop = threading.Event()
        self.thread = None

    def native(self):
        path = self.control / 'host-session-status.json'
        return read_json(path, 8192, age=5)

    def begin(self, client, world=None):
        if not isinstance(client, str) or not re.fullmatch('[0-9a-f]{32}', client):
            raise ValueError('Invalid join attempt')
        if world is not None and (not isinstance(world,str) or not re.fullmatch('[0-9a-f]{32}',world)):
            raise ValueError('Invalid requested scene')
        with self.lock:
            self.cancelled_clients = {k:v for k,v in self.cancelled_clients.items() if v > self.clock()}
            if client in self.cancelled_clients:
                raise ValueError('This join attempt was cancelled')
            self.adopt_reload()
            if self.session and self.session.get('direct') and self.session['phase'] not in ('failed','playing'):
                if world is not None and world != self.session['world']:
                    raise ValueError('Host reloaded another checkpoint')
                if self.session['client'] is None:
                    self.session['client'] = client
            if self.session and self.session['client'] == client:
                return self.public()
            if self.session and self.session['phase'] not in ('failed', 'playing'):
                raise ValueError('Another guest is already joining')
            native = self.native()
            if not native.get('available') or native.get('held'):
                raise ValueError('Host must be in a playable task before joining')
            generation = uuid.uuid4().hex
            self.session = dict(client=client, generation=generation, phase='saving',
                                started=self.clock(), manifest=None, error=None)
            self.lease()
            atomic(self.control / 'host-session-request.json', dict(generation=generation))
            self.record()
            return self.public()

    def adopt_reload(self):
        """Accept only a native-announced selected file, never a client path."""
        native = self.native()
        generation = native.get('generation')
        if (native.get('directReload') is not True or native.get('phase') not in ('entry-saving','saving','reloading','ready')
                or not isinstance(generation,str) or not re.fullmatch('[0-9a-f]{32}',generation)
                or not isinstance(native.get('world'),str) or not re.fullmatch('[0-9a-f]{32}',native['world'])
                or self.session and self.session['generation'] == generation):
            return
        self.session = dict(client=None, generation=generation, phase='saving', started=self.clock(),
                            manifest=None, error=None, direct=True, world=native['world'], mission=native['mission'], entering=native.get('entering') is True)
        self.lease()
        self.record()

    def reload_lobby(self):
        with self.lock:
            s = self.session
            if not s or not s.get('direct') or s['phase'] in ('failed','playing'):
                return None
            # The Unity thread can be busy loading. Announce from the gateway,
            # without requiring another native frame or exposing local paths.
            return dict(schema=1,room=s['generation'],revision=1,world=s['world'],phase='loading',
                        mission=int(s['mission']),title='',limit=0,ready=False,characters=[],reloading=not s.get("entering",False),entering=s.get("entering",False))

    def public(self):
        s = self.session
        return dict(schema=1, generation=s['generation'], phase=s['phase'],
                    manifest=s['manifest'], error=s['error'])

    def status(self, generation):
        with self.lock:
            self.require(generation)
            return self.public()

    def require(self, generation):
        if self.session is None or generation != self.session['generation']:
            raise ValueError('Join attempt expired; start joining again')

    def ready(self, generation, sha, mission):
        with self.lock:
            self.require(generation)
            s = self.session
            if s['phase'] == 'failed':
                return self.public()  # Native guest must end its wait, not retry HTTP 409 forever.
            m = s['manifest']
            if not m or sha != m['sha256'] or mission != m['mission']:
                raise ValueError('Guest loaded a different checkpoint')
            if s['phase'] == 'waiting_guest':
                s['phase'] = 'attaching'
                self.lease()
            elif s['phase'] not in ('attaching', 'connect', 'playing'):
                raise ValueError('Checkpoint is no longer ready')
            return self.public()

    def cancel(self, generation):
        with self.lock:
            self.require(generation)
            if self.session['phase'] != 'failed':
                self.fail('Guest cancelled joining')
            return self.public()

    def cancel_client(self, client):
        # A guest can cancel before a lost join reply reveals the generation.
        # Remember the nonce so a slower in-flight join cannot start afterward.
        if not isinstance(client,str) or not re.fullmatch('[0-9a-f]{32}',client):
            raise ValueError('Invalid join attempt')
        with self.lock:
            self.cancelled_clients[client] = self.clock()+600
            while len(self.cancelled_clients)>256:
                del self.cancelled_clients[next(iter(self.cancelled_clients))]
            if self.session and self.session['client']==client and self.session['phase'] not in ('playing','failed'):
                self.fail('Guest cancelled joining')
            return dict(schema=1,client=client,phase='cancelled')

    def lease(self):
        s = self.session
        atomic(self.control / 'host-session-lease.json', dict(generation=s['generation'],
               attach=s['phase'] in ('attaching', 'connect', 'playing'), cancel=s['phase'] == 'failed',
               error=s.get('error') if s['phase'] == 'failed' else None))

    def record(self):
        s = self.session
        try:
            atomic(self.control / 'gateway-session-status.json',
                   dict(schema=1, generation=s['generation'], phase=s['phase'],
                        error=s['error'], updatedUtc=time.time()))
        except OSError:
            pass

    def fail(self, message):
        self.session.update(phase='failed', error=message)
        self.record()
        self.lease()

    def tick(self):
        with self.lock:
            try:
                self.adopt_reload()
            except (ValueError,OSError,KeyError,TypeError):
                pass  # Existing handoff still has its independent watchdog.
            s = self.session
            if not s or s['phase'] in ('failed', 'playing'):
                return
            try:
                if self.clock() - s['started'] > 600:
                    raise ValueError('Guest load timed out')
                self.lease()
                host = self.native()
                if host.get('generation') != s['generation']:
                    if self.clock() - s['started'] > 10:
                        raise ValueError('Host did not accept this join request')
                    return
                if host.get('phase') == 'failed':
                    raise ValueError(host.get('error') or 'Host checkpoint preparation failed')
                if s['manifest'] and host.get('world') != s['manifest'].get('world'):
                    raise ValueError('Host changed scene during checkpoint handoff')
                exporting = host.get('phase') == 'ready' or s.get('direct') and host.get('phase') == 'reloading'
                if s['phase'] == 'saving' and exporting:
                    if not host.get('held') and not s.get('direct'):
                        raise ValueError('Host checkpoint is no longer paused')
                    profile_file = Path(host['profilePath']).resolve(strict=True)
                    source = Path(host['savePath']).resolve(strict=True)
                    if profile_file.name != 'profileData.save' or not source.is_relative_to(profile_file.parent):
                        raise ValueError('Native checkpoint does not belong to the current profile')
                    relative = source.relative_to(profile_file.parent).as_posix()
                    hub = host.get('hub') is True
                    if hub and (host['mission'] != '-379511569' or relative != 'profileData.save'):
                        raise ValueError('Invalid native ship profile')
                    data, _ = build(profile_file.parent, [] if hub else [relative], hub=hub)
                    after = self.native()
                    identity = ('generation','profilePath','savePath','mission','gameVersion','world','rewind','entering','hub','preparation')
                    if not s.get('direct'): identity += ('phase','held')
                    if any(after.get(k) != host.get(k) for k in identity) or after.get('phase') not in ('reloading','ready'):
                        raise ValueError('Host changed during checkpoint export')
                    s['manifest'] = self.store.publish(data, mission=host['mission'], game_version=host['gameVersion'],
                                                       save_path=relative, generation=s['generation'], world=host.get('world'), rewind=host.get('rewind'), entering=host.get('entering',False),hub=hub,preparation=host.get('preparation',False))
                    s['phase'] = 'waiting_guest'
                elif s['phase'] in ('attaching','connect') and host.get('phase') == 'released':
                    s['phase'] = 'playing'
                elif s['phase'] == 'attaching' and host.get('phase') == 'attaching' and host.get('held'):
                    s['phase'] = 'connect'
                elif s.get('direct') and s['phase'] in ('waiting_guest','attaching') and host.get('phase') == 'reloading':
                    pass  # Guest may finish first; connect still requires host success + pause.
                elif s['phase'] in ('waiting_guest','attaching','connect') and (host.get('phase') not in ('ready','attaching') or not host.get('held')):
                    raise ValueError('Host left the checkpoint before guest readiness')
                self.record()
            except ControlStateExpired:
                # Focus changes / slow native frames must not cancel a valid
                # captured checkpoint. Keep the lease alive, consume no stale
                # state, and retain the overall ten-minute join deadline.
                return
            except PermissionError as error:
                if os.name == 'nt' and error.winerror in (5, 32, 33):
                    return  # Next tick retries; native stale-lease limits still apply.
                self.fail(str(error))
            except (ValueError, OSError, KeyError, TypeError) as error:
                self.fail(str(error))

    def start(self):
        def work():
            while not self.stop.wait(0.25):
                try:
                    self.tick()
                except OSError:
                    # Temporary file sharing errors must not kill the room's
                    # only coordinator. The native lease still expires if the
                    # filesystem stays unavailable; no false resume is sent.
                    continue
        self.thread = threading.Thread(target=work, name='coop-session', daemon=True)
        self.thread.start()

    def close(self):
        self.stop.set()
        if self.thread:
            self.thread.join(5)
        with self.lock:
            if self.session and self.session['phase'] not in ('failed','playing'):
                self.fail('Room gateway closed')


def serve_session(handler):
    prefix = handler.server.room + '/session/'
    if not handler.path.startswith(prefix):
        return False
    broker = getattr(handler.server, 'session_broker', None)
    if broker is None:
        handler.send_error(404)
        return True
    try:
        if handler.command != 'POST' or handler.headers.get('Origin') or handler.headers.get('X-Coop-Session') != '1':
            raise ValueError('Invalid session request')
        payload = json.loads(handler.body(1024))
        if not isinstance(payload,dict):
            raise ValueError('Expected session object')
        suffix = handler.path[len(prefix):]
        if suffix == 'join': result = broker.begin(payload.get('client'),payload.get('world'))
        elif suffix == 'status': result = broker.status(payload.get('generation'))
        elif suffix == 'ready': result = broker.ready(payload.get('generation'),payload.get('sha256'),payload.get('mission'))
        elif suffix == 'cancel': result = broker.cancel(payload.get('generation'))
        elif suffix == 'cancel-client': result = broker.cancel_client(payload.get('client'))
        else:
            handler.send_error(404)
            return True
        handler.plain(json.dumps(result))
    except (ValueError, TypeError, KeyError, OSError):
        handler.send_error(409, 'Checkpoint join is unavailable or expired')
    return True
