"""Real Windows parent/child exit tests against the shipped HTTP gateway."""
import ctypes
import json
import os
from pathlib import Path
import socket
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from ctypes import wintypes
from http.client import HTTPConnection
from gateway_lifetime import LauncherLifetime
from lan_remote import release_health

ROOT = Path(__file__).resolve().parent
PARENT = r'''
import json, os, subprocess, sys
log=open(sys.argv[3],'wb')
environment=dict(os.environ,PYTHONIOENCODING='cp1252',PYTHONUTF8='0')
child=subprocess.Popen([sys.executable,sys.argv[1],'--bind','127.0.0.1',
    '--room-directory',sys.argv[2],'--owner-pid',str(os.getpid())],
    stdin=subprocess.DEVNULL,stdout=log,stderr=log,env=environment,
    creationflags=subprocess.CREATE_NO_WINDOW)
print(json.dumps({'pid':child.pid}),flush=True)
sys.stdin.readline()
'''


@unittest.skipUnless(os.name == 'nt', 'Windows process lifetime')
class GatewayLifetimeTests(unittest.TestCase):
    def test_unrelated_pid_rejected(self):
        with self.assertRaises(ValueError):
            LauncherLifetime(os.getpid())

    def exercise(self, crash):
        with socket.socket() as probe:
            try:
                probe.bind(('127.0.0.1',38477))
            except OSError:
                self.skipTest('Real gateway port occupied; leave existing room untouched')
        runtime=ROOT/'staging'
        runtime.mkdir(parents=True, exist_ok=True)
        folder=Path(tempfile.mkdtemp(prefix='gateway-lifetime-',dir=runtime))
        # Run the shipping gateway in an isolated tree: its control/store paths
        # are relative to __file__, and must never touch the user's live room.
        project=folder/'game'/'ShadowGambitCoop';project.mkdir(parents=True)
        for source in ROOT.glob('*.py'):
            shutil.copy2(source,project/source.name)
        shutil.copy2(ROOT/'skill-protocol.json',project/'skill-protocol.json')
        session=project/'session-next';session.mkdir()
        for source in (ROOT/'session-next').glob('*.py'):
            shutil.copy2(source,session/source.name)
        rooms=folder/'rooms';log=folder/'gateway.log'
        parent=subprocess.Popen([sys.executable,'-c',PARENT,str(project/'lan_remote.py'),str(rooms),str(log)],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,
            creationflags=subprocess.CREATE_NO_WINDOW)
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.OpenProcess.argtypes=(wintypes.DWORD,wintypes.BOOL,wintypes.DWORD)
        kernel.OpenProcess.restype=wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes=(wintypes.HANDLE,wintypes.DWORD)
        kernel.WaitForSingleObject.restype=wintypes.DWORD
        kernel.TerminateProcess.argtypes=(wintypes.HANDLE,wintypes.UINT)
        kernel.CloseHandle.argtypes=(wintypes.HANDLE,)
        handle=None
        try:
            pid=json.loads(parent.stdout.readline())['pid']
            handle=kernel.OpenProcess(0x00100001,False,pid)
            self.assertTrue(handle)
            ready=False
            probe_result='no health response'
            for _ in range(100):
                if kernel.WaitForSingleObject(handle,0)==0:
                    break
                room=rooms/'127.0.0.1.room'
                if room.exists():
                    conn=HTTPConnection('127.0.0.1',38477,timeout=.25)
                    try:
                        conn.request('GET','/'+room.read_text().strip()+'/health')
                        reply=conn.getresponse()
                        body=reply.read()
                        expected=release_health(project).encode('ascii')
                        ready=reply.status==200 and body==expected
                        probe_result=f'status={reply.status} body={body!r} expected={expected!r}'
                    except OSError as error:
                        probe_result=type(error).__name__+': '+str(error)
                    finally:
                        conn.close()
                if ready:
                    break
                time.sleep(.05)
            self.assertTrue(ready,'Actual gateway never became ready: '+probe_result+'; '+log.read_text(encoding='utf-8',errors='replace'))
            self.assertEqual(kernel.WaitForSingleObject(handle,150),258,'Gateway exited before owner')
            if crash:
                parent.terminate()
            else:
                parent.stdin.write('\n');parent.stdin.flush()
            parent.wait(timeout=5)
            self.assertEqual(kernel.WaitForSingleObject(handle,8000),0,'Gateway survived its launcher')
            with socket.socket() as released:
                released.bind(('127.0.0.1',38477))
        finally:
            if parent.poll() is None:
                parent.terminate();parent.wait(timeout=5)
            if handle:
                if kernel.WaitForSingleObject(handle,0)!=0:
                    kernel.TerminateProcess(handle,1)
                    kernel.WaitForSingleObject(handle,3000)
                kernel.CloseHandle(handle)
            for stream in (parent.stdin,parent.stdout,parent.stderr):
                stream.close()

    def test_normal_launcher_exit_closes_real_gateway(self):
        self.exercise(False)

    def test_crashed_launcher_closes_real_gateway(self):
        self.exercise(True)


if __name__ == '__main__':
    unittest.main(verbosity=2)
