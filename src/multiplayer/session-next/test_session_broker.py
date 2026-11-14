import json
import os
import time
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from http.server import ThreadingHTTPServer
from http.client import HTTPConnection
sys.path.insert(0,str(Path(__file__).resolve().parent.parent))
from session_broker import SessionBroker, atomic
from save_transfer import SaveStore
from lan_remote import Remote
from session_join import call, acquire
from install_session_save import install


class JoinTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.control=self.root/'control'
        self.profile=self.root/'host/gameState_00'
        (self.profile/'quickSave').mkdir(parents=True)
        (self.profile/'profileData.save').write_bytes(b'progress')
        (self.profile/'quickSave/scene.save').write_bytes(b'native checkpoint')
        self.now=0
        self.broker=SessionBroker(self.control,SaveStore(self.root/'share'),clock=lambda:self.now)
        self.host(phase='idle',available=True,held=False)

    def host(self,**kw):
        atomic(self.control/'host-session-status.json',dict(schema=1,**kw))

    def ready_host(self,generation):
        self.host(generation=generation,phase='ready',held=True,available=True,
                  profilePath=str(self.profile/'profileData.save'),savePath=str(self.profile/'quickSave/scene.save'),
                  mission='42',gameVersion='test')
        self.broker.tick()

    def begin(self):return self.broker.begin('a'*32)['generation']

    def test_complete_handoff_requires_native_first_frame(self):
        g=self.begin();self.ready_host(g);m=self.broker.status(g)['manifest']
        self.assertEqual(m['generation'],g)
        self.broker.ready(g,m['sha256'],'42')
        self.assertEqual(self.broker.status(g)['phase'],'attaching')
        self.host(generation=g,phase='attaching',held=True);self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'connect')
        self.host(generation=g,phase='released',held=False);self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'playing')

    def test_preparation_without_crew_can_import_host_progress(self):
        # Native admission must publish available during Init_* even with zero crew.
        self.host(phase='idle', available=False, held=False)
        with self.assertRaises(ValueError): self.begin()
        self.assertFalse((self.control/'host-session-request.json').exists())
        self.host(phase='idle', available=True, held=False, world='f'*32)
        g=self.begin()
        self.assertTrue((self.control/'host-session-request.json').exists())
        self.host(generation=g, phase='ready', held=True, available=True,
                  profilePath=str(self.profile/'profileData.save'),
                  savePath=str(self.profile/'profileData.save'), mission='-379511569',
                  gameVersion='test', hub=True, preparation=True, world='f'*32)
        self.broker.tick()
        m=self.broker.status(g)['manifest']
        self.assertTrue(m['preparation'])
        self.assertTrue(m['hub'])
        self.broker.ready(g,m['sha256'],'-379511569')
        self.host(generation=g,phase='released',held=False,world='f'*32)
        self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'playing')

    def announce_reload(self,g='e'*32,phase='reloading',held=False):
        self.host(generation=g,phase=phase,held=held,available=False,directReload=True,world='f'*32,
                  profilePath=str(self.profile/'profileData.save'),savePath=str(self.profile/'quickSave/scene.save'),
                  mission='42',gameVersion='test')

    def test_entry_waits_for_original_write_and_publishes_early(self):
        self.announce_reload(phase='entry-saving')
        path=self.control/'host-session-status.json';host=json.loads(path.read_text());host.update(entering=True,savePath=None,profilePath=None,held=True);atomic(path,host)
        self.broker.tick();g='e'*32
        self.assertEqual(self.broker.status(g)['phase'],'saving')
        self.assertTrue(self.broker.reload_lobby()['entering'])
        self.assertFalse(self.broker.reload_lobby()['reloading'])
        self.assertIsNone(self.broker.status(g)['manifest'])
        host.update(phase='ready',profilePath=str(self.profile/'profileData.save'),savePath=str(self.profile/'quickSave/scene.save'));atomic(path,host)
        self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'waiting_guest')
        self.assertTrue(self.broker.status(g)['manifest']['entering'])
        self.assertFalse((self.control/'host-session-request.json').exists())

    def test_enemy_reload_metadata_reaches_manifest(self):
        self.announce_reload()
        path=self.control/'host-session-status.json'
        status=json.loads(path.read_text())
        frame=dict(schema=1,enemy=True,showEffect=True,intensity=2,names=['phase'],states=[3])
        status['rewind']=frame;atomic(path,status)
        self.broker.tick()
        self.assertEqual(self.broker.status('e'*32)['manifest']['rewind'],frame)

    def test_reload_downloads_original_while_host_still_loading(self):
        self.announce_reload();self.broker.tick();g='e'*32
        self.assertEqual(self.broker.status(g)['phase'],'waiting_guest')
        self.assertFalse((self.control/'host-session-request.json').exists(), 'no fresh native save requested')
        announcement=self.broker.reload_lobby()
        self.assertTrue(announcement['reloading']);self.assertEqual(announcement['world'],'f'*32)
        self.assertNotIn('savePath',announcement)
        self.assertEqual(self.broker.begin('b'*32,'f'*32)['generation'],g)
        with self.assertRaises(ValueError):self.broker.begin('c'*32,'d'*32)
        with self.assertRaises(ValueError):self.broker.begin('c'*32,'f'*32)
        m=self.broker.status(g)['manifest'];self.broker.ready(g,m['sha256'],'42');self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'attaching','guest finishing first must wait for native host success')
        self.announce_reload(phase='ready',held=True);self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'attaching')
        self.host(generation=g,phase='attaching',held=True,world='f'*32);self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'connect')
        self.host(generation=g,phase='released',held=False,world='f'*32);self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'playing');self.assertIsNone(self.broker.reload_lobby())

    def test_fast_host_reload_still_exports_once_and_waits_for_guest(self):
        self.announce_reload(phase='ready',held=True);self.broker.tick();m=self.broker.status('e'*32)['manifest']
        self.broker.tick();self.assertEqual(self.broker.status('e'*32)['manifest'],m)
        self.assertEqual(self.broker.status('e'*32)['phase'],'waiting_guest')
        self.assertFalse(json.loads((self.control/'host-session-lease.json').read_text())['attach'])

    def test_reload_native_failure_reaches_waiting_guest(self):
        self.announce_reload();self.broker.tick();self.broker.begin('b'*32,'f'*32)
        self.host(generation='e'*32,phase='failed',error='Host load failed',world='f'*32);self.broker.tick()
        self.assertEqual(self.broker.status('e'*32)['phase'],'failed');self.assertIsNone(self.broker.reload_lobby())

    def test_new_reload_supersedes_old_transaction_without_old_cancel_affecting_it(self):
        self.announce_reload();self.broker.tick();self.broker.begin('b'*32,'f'*32)
        self.announce_reload(g='d'*32);self.broker.tick()
        self.assertEqual(self.broker.status('d'*32)['phase'],'waiting_guest')
        with self.assertRaises(ValueError):self.broker.cancel('e'*32)
        self.assertFalse(json.loads((self.control/'host-session-lease.json').read_text())['cancel'])

    def test_direct_reload_cannot_export_external_path(self):
        outside=self.root/'unrelated.save';outside.write_bytes(b'private')
        self.announce_reload()
        native=self.broker.native();native['savePath']=str(outside);atomic(self.control/'host-session-status.json',native)
        self.broker.tick();self.assertEqual(self.broker.status('e'*32)['phase'],'failed')

    def test_wrong_hash_or_mission_never_releases_host(self):
        g=self.begin();self.ready_host(g);m=self.broker.status(g)['manifest']
        for sha,mission in [('0'*64,'42'),(m['sha256'],'43')]:
            with self.assertRaises(ValueError):self.broker.ready(g,sha,mission)
        lease=json.loads((self.control/'host-session-lease.json').read_text())
        self.assertFalse(lease['attach'])

    def test_scene_epoch_survives_export_and_rejects_reload_of_same_mission(self):
        g=self.begin()
        self.host(generation=g,phase='ready',held=True,available=True,world='c'*32,
                  profilePath=str(self.profile/'profileData.save'),savePath=str(self.profile/'quickSave/scene.save'),
                  mission='42',gameVersion='test')
        self.broker.tick();manifest=self.broker.status(g)['manifest']
        self.assertEqual(manifest['world'],'c'*32)
        self.host(generation=g,phase='ready',held=True,world='d'*32,mission='42')
        self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'failed')
        self.assertTrue(json.loads((self.control/'host-session-lease.json').read_text())['cancel'])

    def test_old_generation_and_concurrent_guest_rejected(self):
        g=self.begin()
        self.assertEqual(self.broker.begin('a'*32)['generation'],g)
        with self.assertRaises(ValueError):self.broker.begin('b'*32)
        with self.assertRaises(ValueError):self.broker.ready('b'*32,'0'*64,'42')

    def test_timestamp_rounding_is_fresh_but_stale_or_future_frames_are_not(self):
        path=self.control/'host-session-status.json'
        from unittest.mock import patch
        with patch('session_broker.time.time',return_value=1000):
            os.utime(path,(1000.0000002,1000.0000002))
            self.assertTrue(self.broker.native()['available'])
            for stamp in (994,1002):
                os.utime(path,(stamp,stamp))
                with self.assertRaises(ValueError):self.broker.native()

    def test_cancel_before_lost_join_reply_releases_same_attempt_only(self):
        g=self.begin()
        self.broker.cancel_client('b'*32)
        self.assertNotEqual(self.broker.status(g)['phase'],'failed')
        self.broker.cancel_client('a'*32)
        self.assertEqual(self.broker.status(g)['phase'],'failed')
        self.assertTrue(json.loads((self.control/'host-session-lease.json').read_text())['cancel'])
        with self.assertRaises(ValueError):self.broker.begin('a'*32)

    def test_cancel_before_join_arrives_prevents_late_pause(self):
        self.broker.cancel_client('a'*32)
        with self.assertRaises(ValueError):self.begin()
        self.assertIsNone(self.broker.session)
        self.assertFalse((self.control/'host-session-request.json').exists())

    def test_expired_host_and_timeout_cancel_wait(self):
        g=self.begin();self.now=601;self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'failed')
        self.assertTrue(json.loads((self.control/'host-session-lease.json').read_text())['cancel'])

    def test_resumed_host_cannot_publish_saved_world(self):
        g=self.begin();self.ready_host(g)
        self.host(generation=g,phase='released',held=False);self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'failed')

    def test_slow_native_updates_keep_checkpoint_held_until_refresh(self):
        g=self.begin();self.ready_host(g);m=self.broker.status(g)['manifest']
        path=self.control/'host-session-status.json'
        os.utime(path,(1,1))
        self.now=60;self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'waiting_guest')
        self.assertFalse(json.loads((self.control/'host-session-lease.json').read_text())['cancel'])
        self.broker.ready(g,m['sha256'],'42')
        self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'attaching')
        self.host(generation=g,phase='attaching',held=True)
        self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'connect')

    def test_failed_ready_returns_terminal_phase_and_preserves_reason(self):
        g=self.begin();self.ready_host(g);m=self.broker.status(g)['manifest']
        self.broker.fail('Host changed during checkpoint export')
        result=self.broker.ready(g,m['sha256'],'42')
        self.assertEqual(result['phase'],'failed')
        self.assertEqual(result['error'],'Host changed during checkpoint export')
        evidence=json.loads((self.control/'gateway-session-status.json').read_text())
        self.assertEqual(evidence['error'],result['error'])
        self.assertEqual(self.broker.cancel(g)['error'],result['error'])
        self.broker.cancel_client('a'*32)
        self.assertEqual(self.broker.status(g)['error'],result['error'])

    def test_stalled_native_update_still_has_join_deadline(self):
        g=self.begin();self.ready_host(g)
        os.utime(self.control/'host-session-status.json',(1,1))
        self.now=601;self.broker.tick()
        self.assertEqual(self.broker.status(g)['phase'],'failed')

    def test_gateway_restart_does_not_offer_old_manifest(self):
        g=self.begin();self.ready_host(g)
        other=SessionBroker(self.control,self.broker.store)
        with self.assertRaises(ValueError):other.begin('c'*32)
        with self.assertRaises(ValueError):other.status(g)

    @unittest.skipUnless(os.name == 'nt', 'Windows sharing semantics')
    def test_coordinator_survives_a_control_file_lock(self):
        g = self.begin()
        self.host(generation=g, phase='ready', held=True, available=True,
                  profilePath=str(self.profile/'profileData.save'),
                  savePath=str(self.profile/'quickSave/scene.save'), mission='42', gameVersion='test')
        # Keep the legacy reader open longer than one bounded publication retry.
        lease = (self.control/'host-session-lease.json').open('rb')
        release = threading.Timer(.7, lease.close)
        release.start()
        import traceback
        failures = []
        original_fail = self.broker.fail
        def record_failure(message):
            failures.append(traceback.format_exc())
            original_fail(message)
        self.broker.fail = record_failure
        self.broker.start()
        try:
            until = time.monotonic() + 4
            while time.monotonic() < until:
                if self.broker.status(g)['phase'] == 'waiting_guest':
                    break
                time.sleep(.03)
            self.assertTrue(self.broker.thread.is_alive())
            self.assertEqual(self.broker.status(g)['phase'], 'waiting_guest', failures)
        finally:
            release.join()
            lease.close()
            self.broker.close()

    def test_post_selection_native_save_is_adopted_before_ready(self):
        self.host(generation='e'*32, phase='saving', directReload=True, entering=False,
                  world='f'*32, mission='42', held=False)
        self.broker.tick()
        self.assertEqual(self.broker.session['generation'], 'e'*32)
        self.assertEqual(self.broker.session['phase'], 'saving')
        self.assertTrue(self.broker.reload_lobby()['reloading'])
        self.assertFalse(self.broker.reload_lobby()['entering'])
        self.assertFalse((self.control/'host-session-request.json').exists())
        self.host(generation='e'*32, phase='ready', directReload=True, entering=False,
                  world='f'*32, mission='42', held=True, gameVersion='test',
                  profilePath=str(self.profile/'profileData.save'),savePath=str(self.profile/'quickSave/scene.save'))
        self.broker.tick()
        self.assertEqual(self.broker.session['phase'], 'waiting_guest')
        self.assertEqual(self.broker.begin('b'*32,'f'*32)['generation'], 'e'*32)

    def test_real_http_acquire_install_and_receipts(self):
        server=ThreadingHTTPServer(('127.0.0.1',0),Remote)
        server.room='/'+'x'*24;server.session_broker=self.broker;server.save_store=self.broker.store
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        stop=threading.Event()
        def native_simulator():
            while not stop.wait(.02):
                with self.broker.lock:
                    s=self.broker.session
                    if s and s['phase']=='saving':self.ready_host(s['generation'])
        simulator=threading.Thread(target=native_simulator,daemon=True);simulator.start()
        try:
            url=f'http://127.0.0.1:{server.server_port}'+server.room
            manifest,package=acquire(url,self.root/'download','test',timeout=5)
            account=self.root/'guest/user_steam_00000000000000001';account.mkdir(parents=True)
            installed=install(package.read_bytes(),manifest,account,self.root/'guest-control',url,lambda:False,wait_for_host=True)
            req=json.loads(Path(installed['request']).read_text())
            self.assertTrue(req['waitForHost']);self.assertEqual(req['bundleSha256'],manifest['sha256'])
            result=call(url,'ready',dict(generation=manifest['generation'],sha256=manifest['sha256'],mission='42'))
            self.assertEqual(result['phase'],'attaching')
            c=HTTPConnection('127.0.0.1',server.server_port)
            c.request('POST',server.room+'/session/join','{}',{'Origin':'http://unrelated','X-Coop-Session':'1'})
            self.assertEqual(c.getresponse().status,409);c.close()
        finally:
            stop.set();simulator.join(2);server.shutdown();server.server_close();thread.join(2)

if __name__=='__main__':unittest.main()
