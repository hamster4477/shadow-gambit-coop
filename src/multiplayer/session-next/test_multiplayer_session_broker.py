"""Shared-file checkpoint barriers with distinct peers, without game access."""
import concurrent.futures
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from session_broker import SessionBroker, atomic, serve_session
from save_transfer import SaveStore


def ident(number):
    return format(number, '032x')


class MultiplayerSessionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.control = self.root / 'control'
        self.profile = self.root / 'profile'
        (self.profile / 'quickSave').mkdir(parents=True)
        (self.profile / 'profileData.save').write_bytes(b'profile progress')
        (self.profile / 'quickSave/scene.save').write_bytes(b'checkpoint')
        self.now = 0
        self.world = 'f' * 32
        self.store = SaveStore(self.root / 'share')
        self.store.publish = Mock(wraps=self.store.publish)
        self.broker = SessionBroker(self.control, self.store, clock=lambda: self.now)
        self.host(phase='idle', available=True, held=False)

    def host(self, **values):
        atomic(self.control / 'host-session-status.json', dict(schema=1, world=self.world, **values))

    def join(self, number, attempt=None):
        return self.broker.begin(ident(attempt or 1000 + number), self.world, ident(number))

    def export(self, generation, *, direct=False):
        self.host(generation=generation, phase='reloading' if direct else 'ready',
                  held=not direct, available=not direct, directReload=direct,
                  profilePath=str(self.profile / 'profileData.save'),
                  savePath=str(self.profile / 'quickSave/scene.save'),
                  mission='42', gameVersion='test')
        self.broker.tick()
        return self.broker.session['manifest']

    def ready(self, generation, number):
        manifest = self.broker.session['manifest']
        return self.broker.ready(generation, manifest['sha256'], '42', ident(number))

    def lease(self):
        return json.loads((self.control / 'host-session-lease.json').read_text())

    def release(self, generation):
        self.host(generation=generation, phase='attaching', held=True)
        self.broker.tick()
        self.assertEqual(self.broker.session['phase'], 'connect')
        self.host(generation=generation, phase='released', held=False, available=True)
        self.broker.tick()
        self.assertEqual(self.broker.session['phase'], 'playing')

    def test_129_peers_share_one_file_and_wait_for_last_receipt(self):
        peers = range(1, 130)
        for peer in peers:
            self.broker.touch_peer(ident(peer))
        generation = self.join(1)['generation']
        request = (self.control / 'host-session-request.json').read_bytes()
        for peer in list(peers)[1:]:
            self.assertEqual(self.join(peer)['generation'], generation)
        self.assertEqual((self.control / 'host-session-request.json').read_bytes(), request)
        manifest = self.export(generation)
        self.assertEqual(self.store.publish.call_count, 1)
        for peer in list(peers)[:-1]:
            state = self.ready(generation, peer)
            self.assertEqual(state['phase'], 'waiting_guest')
            self.assertFalse(self.lease()['attach'])
        state = self.ready(generation, 129)
        self.assertEqual(state['phase'], 'attaching')
        self.assertEqual(state['participants'], 129)
        self.assertEqual(state['readyCount'], 129)
        self.assertEqual(state['waitingCount'], 0)
        self.assertEqual(set(self.lease()['readyPeers']), {ident(peer) for peer in peers})
        self.assertNotIn('readyPeers', state)
        self.assertLess(len(json.dumps(state)), 8192)
        self.assertEqual(state['manifest'], manifest)
        self.release(generation)
        self.assertEqual(self.store.publish.call_count, 1)

    def test_concurrent_joiners_share_generation_and_all_are_counted(self):
        with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
            replies = list(pool.map(self.join, range(1, 25)))
        self.assertEqual(len({reply['generation'] for reply in replies}), 1)
        generation = replies[0]['generation']
        self.export(generation)
        self.assertEqual(self.broker.status(generation, ident(1))['participants'], 24)
        for peer in range(1, 24):
            self.ready(generation, peer)
        self.assertFalse(self.lease()['attach'])
        self.ready(generation, 24)
        self.assertTrue(self.lease()['attach'])

    def test_active_peer_must_join_and_ack_even_if_it_did_not_start_capture(self):
        self.broker.touch_peer(ident(2))
        generation = self.join(1)['generation']
        self.export(generation)
        self.ready(generation, 1)
        self.assertFalse(self.lease()['attach'])
        with self.assertRaises(ValueError):
            self.ready(generation, 2)
        announcement = self.broker.reload_lobby()
        self.assertEqual(announcement['room'], generation)
        self.assertEqual(announcement['world'], self.world)
        self.assertEqual(announcement['mission'], 42)
        self.join(2)
        self.ready(generation, 2)
        self.assertTrue(self.lease()['attach'])

    def test_cancelling_one_peer_keeps_other_loaded_peers_valid(self):
        generation = self.join(1)['generation']
        self.join(2)
        self.join(3)
        self.export(generation)
        self.ready(generation, 1)
        result = self.broker.cancel(generation, ident(2))
        self.assertEqual(result['phase'], 'failed')
        self.assertFalse(self.lease()['cancel'])
        self.assertFalse(self.lease()['attach'])
        self.ready(generation, 3)
        self.assertTrue(self.lease()['attach'])
        self.assertEqual(self.broker.status(generation, ident(1))['participants'], 2)
        self.assertEqual(set(self.lease()['readyPeers']), {ident(1), ident(3)})
        self.assertEqual(self.broker.status(generation, ident(2))['phase'], 'failed')

    def test_last_cancel_releases_native_hold_as_cancel_not_attach(self):
        generation = self.join(1)['generation']
        self.join(2)
        self.export(generation)
        self.broker.cancel(generation, ident(1))
        self.broker.cancel(generation, ident(2))
        self.assertTrue(self.lease()['cancel'])
        self.assertFalse(self.lease()['attach'])

    def test_old_attempt_cancel_cannot_cancel_another_peer(self):
        generation = self.join(1)['generation']
        self.join(2)
        self.broker.cancel_client(ident(1001), ident(2))
        self.export(generation)
        self.ready(generation, 1)
        self.ready(generation, 2)
        self.assertTrue(self.lease()['attach'])

    def test_cancel_before_join_tombstones_only_that_peer_and_attempt(self):
        self.broker.cancel_client(ident(1001), ident(1))
        with self.assertRaises(ValueError):
            self.join(1)
        self.assertNotIn(ident(1), self.broker.peer_seen)
        self.assertEqual(self.join(2, attempt=1001)['phase'], 'saving')

    def test_attempt_retry_does_not_create_checkpoint_after_playing(self):
        generation = self.join(1)['generation']
        self.export(generation)
        self.ready(generation, 1)
        self.release(generation)
        result = self.join(1)
        self.assertEqual(result['phase'], 'playing')
        self.assertEqual(result['generation'], generation)
        self.assertEqual(self.store.publish.call_count, 1)

    def test_replacement_attempt_waits_for_new_generation(self):
        generation = self.join(1)['generation']
        self.join(2)
        with self.assertRaises(ValueError):
            self.join(1, attempt=9001)
        self.broker.cancel(generation, ident(1))
        with self.assertRaises(ValueError):
            self.join(1, attempt=9001)
        self.export(generation)
        self.ready(generation, 2)
        self.release(generation)
        new_generation = self.join(1, attempt=9001)['generation']
        self.assertNotEqual(new_generation, generation)
        self.broker.cancel_client(ident(1001), ident(1))
        self.assertNotEqual(self.broker.status(new_generation, ident(1))['phase'], 'failed')

    def test_late_peer_cannot_revoke_published_attach(self):
        generation = self.join(1)['generation']
        self.export(generation)
        self.ready(generation, 1)
        with self.assertRaises(ValueError):
            self.join(2)
        self.assertTrue(self.lease()['attach'])
        self.release(generation)
        next_generation = self.join(2)['generation']
        self.assertNotEqual(next_generation, generation)
        self.export(next_generation)
        self.assertEqual(self.broker.reload_lobby()['room'], next_generation)
        self.ready(next_generation, 2)
        self.assertFalse(self.lease()['attach'])
        self.join(1, attempt=9001)
        self.ready(next_generation, 1)
        self.assertTrue(self.lease()['attach'])

    def test_loading_peer_has_longer_lease_and_only_expired_peer_leaves(self):
        generation = self.join(1)['generation']
        self.join(2)
        self.export(generation)
        self.ready(generation, 1)
        self.now = 31
        self.broker.status(generation, ident(1))
        self.broker.tick()
        self.assertEqual(self.broker.status(generation, ident(1))['participants'], 2)
        self.assertFalse(self.lease()['attach'])
        self.now = 181
        self.broker.status(generation, ident(1))
        self.broker.tick()
        self.assertEqual(self.broker.status(generation, ident(1))['participants'], 1)
        self.assertTrue(self.lease()['attach'])
        self.assertEqual(self.broker.status(generation, ident(2))['phase'], 'failed')

    def test_idle_peers_expire_without_reserving_future_checkpoint(self):
        self.broker.touch_peer(ident(2))
        self.now = 31
        generation = self.join(1)['generation']
        self.export(generation)
        self.assertEqual(self.ready(generation, 1)['participants'], 1)
        self.assertTrue(self.lease()['attach'])

    def test_disconnect_removes_only_that_peer_from_barrier(self):
        generation = self.join(1)['generation']
        self.join(2)
        self.export(generation)
        self.ready(generation, 1)
        self.broker.leave_peer(ident(2))
        self.assertTrue(self.lease()['attach'])
        self.assertFalse(self.lease()['cancel'])

    def test_training_peer_does_not_block_shared_checkpoint(self):
        self.broker.touch_peer(ident(2))
        atomic(self.control / 'peers' / ident(2) / 'training.json', dict(schema=1, active=True))
        generation = self.join(1)['generation']
        self.export(generation)
        self.assertEqual(self.ready(generation, 1)['participants'], 1)
        self.assertTrue(self.lease()['attach'])
        with self.assertRaises(ValueError):
            self.join(2)
        self.release(generation)
        atomic(self.control / 'peers' / ident(2) / 'training.json', dict(schema=1, active=False))
        next_generation = self.join(2)['generation']
        self.assertNotEqual(next_generation, generation)

    def test_entering_training_removes_only_that_waiting_peer(self):
        generation = self.join(1)['generation']
        self.join(2)
        self.export(generation)
        self.ready(generation, 1)
        atomic(self.control / 'peers' / ident(2) / 'training.json', dict(schema=1, active=True))
        self.broker.tick()
        self.assertTrue(self.lease()['attach'])
        self.assertFalse(self.lease()['cancel'])
        self.assertEqual(self.broker.status(generation, ident(2))['phase'], 'failed')

    def test_direct_host_reload_announces_and_waits_for_all_known_peers(self):
        for peer in range(1, 4):
            self.broker.touch_peer(ident(peer))
        generation = 'e' * 32
        manifest = self.export(generation, direct=True)
        self.assertIsNotNone(manifest)
        self.assertFalse((self.control / 'host-session-request.json').exists())
        self.assertEqual(self.broker.reload_lobby()['room'], generation)
        for peer in range(1, 4):
            self.assertEqual(self.join(peer)['generation'], generation)
            self.ready(generation, peer)
            self.assertEqual(self.lease()['attach'], peer == 3)
        self.release(generation)

    def test_bad_receipts_do_not_release_host(self):
        generation = self.join(1)['generation']
        self.export(generation)
        manifest = self.broker.session['manifest']
        for sha, mission, peer in [('0' * 64, '42', ident(1)),
                                   (manifest['sha256'], '43', ident(1)),
                                   (manifest['sha256'], '42', ident(2))]:
            with self.assertRaises(ValueError):
                self.broker.ready(generation, sha, mission, peer)
        with self.assertRaises(ValueError):
            self.broker.cancel('d' * 32, ident(1))
        with self.assertRaises(ValueError):
            self.broker.begin(ident(1002), 'd' * 32, ident(2))
        self.assertFalse(self.lease()['attach'])
        self.assertFalse(self.lease()['cancel'])

    def test_legacy_requests_cannot_cancel_or_ack_multiplayer_barrier(self):
        generation = self.join(1)['generation']
        self.export(generation)
        manifest = self.broker.session['manifest']
        for call in [lambda: self.broker.cancel(generation),
                     lambda: self.broker.cancel_client(ident(1001)),
                     lambda: self.broker.status(generation),
                     lambda: self.broker.ready(generation, manifest['sha256'], '42'),
                     lambda: self.broker.begin(ident(1002))]:
            with self.assertRaises(ValueError):
                call()
        self.assertFalse(self.lease()['attach'])
        self.assertFalse(self.lease()['cancel'])

    def test_http_adapter_passes_stable_peer_and_isolates_leave(self):
        broker = self.broker
        class Handler:
            server = type('Server', (), dict(room='/room', session_broker=broker))()
            command = 'POST'
            def __init__(self, suffix, peer, value):
                self.path = '/room/session/' + suffix
                self.headers = {'X-Coop-Session': '1', 'X-Coop-Peer': peer}
                self.value = value
                self.code = 200
            def body(self, limit):
                value = json.dumps(self.value).encode()
                assert len(value) <= limit
                return value
            def plain(self, value):
                self.result = json.loads(value)
            def send_error(self, code, *args):
                self.code = code
        one = Handler('join', ident(1), {'client': ident(1001), 'world': self.world})
        self.assertTrue(serve_session(one))
        generation = one.result['generation']
        two = Handler('join', ident(2), {'client': ident(1002), 'world': self.world})
        serve_session(two)
        self.assertEqual(two.result['generation'], generation)
        self.export(generation)
        self.ready(generation, 1)
        leave = Handler('leave', ident(2), {})
        serve_session(leave)
        self.assertEqual(leave.code, 200)
        self.assertTrue(self.lease()['attach'])
        invalid = Handler('cancel', 'not-a-peer', {'generation': generation})
        serve_session(invalid)
        self.assertEqual(invalid.code, 409)
        self.assertFalse(self.lease()['cancel'])


if __name__ == '__main__':
    unittest.main()
