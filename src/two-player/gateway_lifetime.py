"""Keep a Steam-owned gateway inside the exact launcher's process lifetime."""
import ctypes
import os
import threading
from ctypes import wintypes


class LauncherLifetime:
    def __init__(self, owner_pid):
        # A handle identifies the original parent even after its PID is reused.
        if os.name != 'nt' or owner_pid <= 0 or owner_pid != os.getppid():
            raise ValueError('Gateway owner must be its Windows parent process')
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel.CloseHandle.restype = wintypes.BOOL
        self._kernel = kernel
        self._handle = kernel.OpenProcess(0x00100000, False, owner_pid)
        if not self._handle:
            raise ctypes.WinError(ctypes.get_last_error())
        self._started = False

    def watch(self, stop):
        if self._started:
            raise RuntimeError('Gateway lifetime watcher already started')
        self._started = True

        def wait():
            try:
                result = self._kernel.WaitForSingleObject(self._handle, 0xFFFFFFFF)
                if result in (0, 0xFFFFFFFF):
                    stop()
            finally:
                self._kernel.CloseHandle(self._handle)

        threading.Thread(target=wait, name='coop-launcher-lifetime', daemon=True).start()
