import copy
import json
import os
from pathlib import Path
import tempfile
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace
import unittest
from lobby_broker import (LobbyBroker, entry_ready, peer_path, peers, planning_status,
                          serve_lobby, touch_peer, training_status)
from session_broker import atomic

class MultiplayerLobbyTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.root=Path(self.tmp.name)
        self.ids=[format(i+1,'032x') for i in range(64)]
        self.frame=dict(schema=1,room='a'*32,world='b'*32,revision=1,phase='crew',mission=42,title='trial',limit=8,ready=True,characters=[])
        atomic(self.root/'host-lobby.json',self.frame)
        self.broker=LobbyBroker(self.root)
    def tearDown(self):self.tmp.cleanup()
    def request(self,peer,path,data=None):
        h=SimpleNamespace(path='/room/'+path,command='GET' if data is None else 'POST',headers={'X-Coop-Peer':peer,'X-Coop-Lobby':'1'},server=SimpleNamespace(room='/room',session_broker=SimpleNamespace(control=self.root,lock=threading.Lock(),lobby=self.broker,reload_lobby=lambda:None)),status=200)
        h.body=lambda limit:json.dumps(data)
        h.plain=lambda value:setattr(h,'result',json.loads(value))
        h.send_error=lambda code,*args:setattr(h,'status',code)
        self.assertTrue(serve_lobby(h));return h
    def test_64_presence_and_entry_receipts_are_independent(self):
        for peer in self.ids:
            touch_peer(self.root,peer)
            entry_ready(self.broker,self.root,dict(operation='entry-ready',world='b'*32,mission=42),peer)
        self.assertEqual(set(peers(self.root)),set(self.ids))
        self.assertEqual(len(list((self.root/'peers').glob('*/entry.json'))),64)
        # A stale third client does not replace another client's ready record.
        with self.assertRaises(ValueError):entry_ready(self.broker,self.root,dict(operation='entry-ready',world='c'*32,mission=42),self.ids[2])
        self.assertEqual(json.loads(peer_path(self.root,self.ids[0],'entry').read_text())['world'],'b'*32)
    def test_story_ack_sequences_do_not_overwrite_each_other(self):
        atomic(self.root/'host-story.json',dict(schema=1,world='b'*32,last=4))
        for n,peer in enumerate(self.ids[:12]):
            value=dict(schema=1,world='b'*32,seen=n%5,request=peer,ready=[],playing=bool(n%2),triggers=[])
            self.assertEqual(self.request(peer,'story',value).status,200)
            atomic(self.root/'peers'/peer/'story-receipt.json',dict(schema=1,world='b'*32,seen=n))
        for n,peer in enumerate(self.ids[:12]):
            self.assertEqual(json.loads(peer_path(self.root,peer,'story').read_text())['seen'],n%5)
            self.assertEqual(self.request(peer,'story').result['guestSeen'],n)
        self.assertFalse((self.root/'guest-story.json').exists())
    def test_training_any_player_blocks_others_until_all_return(self):
        atomic(self.root/'training-host.json',dict(schema=1,active=False))
        for peer in self.ids[:4]:touch_peer(self.root,peer)
        training_status(self.root,dict(operation='training',active=True),self.ids[1])
        training_status(self.root,dict(operation='training',active=True),self.ids[2])
        self.assertTrue(training_status(self.root,dict(operation='training',active=False),self.ids[0])['host'])
        training_status(self.root,dict(operation='training',active=False),self.ids[1])
        self.assertTrue(training_status(self.root,dict(operation='training',active=False),self.ids[0])['host'])
        training_status(self.root,dict(operation='training',active=False),self.ids[2])
        self.assertFalse(training_status(self.root,dict(operation='training',active=False),self.ids[0])['host'])
    def test_expired_player_not_counted(self):
        for peer in self.ids[:3]:touch_peer(self.root,peer)
        os.utime(peer_path(self.root,self.ids[1],'presence'),(1,1))
        self.assertEqual(set(peers(self.root)),{self.ids[0],self.ids[2]})
    def test_projection_hides_other_players_and_preserves_spectator(self):
        self.frame.update(phase='playing',mission=-379511569,shipHost='A'*64,shipGuest='B'*64,limit=3,shipOwners={'A'*64:'host','B'*64:self.ids[0],'C'*64:self.ids[1]},characters=[dict(id=c*64,owner=o,selected=True,unavailable=False) for c,o in [('A','H'),('B','G'),('C','G')]])
        atomic(self.root/'host-lobby.json',self.frame)
        for n,peer in enumerate(self.ids[:3]):
            frame=self.broker.state(peer)
            self.assertNotIn('shipOwners',frame)
            self.assertEqual(frame['shipGuest'],None if n==2 else ('B' if n==0 else 'C')*64)
            self.assertEqual([r['owner'] for r in frame['characters']],['H','G','H'] if n==0 else ['H','H','G'] if n==1 else ['H','H','H'])
    def test_planning_mailboxes_and_barrier_ack_are_per_player(self):
        base=dict(operation='planning',client='c'*32,world='b'*32,revision=1,active=True,queues=[],request=None,barrier='d'*32,executed=None,requestActor=None,executedActor=None)
        atomic(self.root/'host-planning.json',base)
        for n,peer in enumerate(self.ids[:12]):
            payload=dict(base,client=peer,barrier=None if n==11 else 'd'*32)
            planning_status(self.root,payload,peer)
        self.assertIsNone(json.loads(peer_path(self.root,self.ids[11],'planning').read_text())['barrier'])
        self.assertEqual(json.loads(peer_path(self.root,self.ids[0],'planning').read_text())['barrier'],'d'*32)
    def test_concurrent_selections_reach_native_serially_with_identity(self):
        done=threading.Event();seen=[]
        def native():
            while not done.is_set():
                try:
                    v=json.loads((self.root/'host-lobby-command.json').read_text())
                    if v['request'] not in [p[0] for p in seen]:
                        seen.append((v['request'],v['peer']))
                        atomic(self.root/'host-lobby-reply.json',dict(request=v['request'],accepted=True,reason='accepted'))
                except (OSError,ValueError):pass
                time.sleep(.002)
        worker=threading.Thread(target=native);worker.start()
        def select(peer):return self.broker.select(dict(request=peer,room='a'*32,revision=1,character='A'*64,selected=True,peer=None),peer)
        try:
            with ThreadPoolExecutor(max_workers=12) as pool:answers=list(pool.map(select,self.ids[:12]))
            self.assertTrue(all(a['accepted'] for a in answers));self.assertEqual(set(seen),{(p,p) for p in self.ids[:12]})
        finally:done.set();worker.join()
    def test_bad_peer_never_becomes_a_path(self):
        self.assertEqual(self.request('../escape','lobby').status,409)
        with self.assertRaises(ValueError):peer_path(self.root,'../escape','entry')

if __name__=='__main__':unittest.main(verbosity=2)
