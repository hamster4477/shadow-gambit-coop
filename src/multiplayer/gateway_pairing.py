"""Short-lived owner-created invitation; never disclose rooms by hostname alone."""
import hashlib
import hmac
import json
import re
import threading
import time


class RoomPairing:
    def __init__(self, path, room, clock=time.time):
        self.path, self.room, self.clock = path, room, clock
        self.lock = threading.Lock()
        self.failures = []

    def redeem(self, code):
        with self.lock:
            now = self.clock()
            self.failures = [t for t in self.failures if now-t < 60]
            if len(self.failures) >= 5:
                return None
            try:
                if self.path.stat().st_size > 2048:
                    raise ValueError()
                value = json.loads(self.path.read_text(encoding='utf-8-sig'))
                valid = (re.fullmatch(r'[0-9]{8}', code) is not None
                         and isinstance(value['expires'], (int, float))
                         and now < value['expires'] <= now+310
                         and hmac.compare_digest(value['roomHash'], hashlib.sha256(self.room[1:].encode('ascii')).hexdigest())
                         and hmac.compare_digest(value['code'], code))
                if valid:
                    return self.room[1:]
            except (OSError, ValueError, KeyError, TypeError):
                pass
            self.failures.append(now)
            return None


def serve_pairing(handler):
    if handler.path != '/coop/pair':
        return False
    pairing = getattr(handler.server, 'pairing', None)
    if handler.command != 'POST' or pairing is None:
        handler.send_error(404)
        return True
    try:
        code = handler.body(8)
        token = pairing.redeem(code)
    except (ValueError, TimeoutError, UnicodeError):
        token = None
    if token is None:
        handler.send_error(404)
    else:
        handler.plain('COOP_PAIR_1 '+token)
    return True
