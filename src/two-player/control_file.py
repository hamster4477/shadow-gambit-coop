"""Small control files shared with the native game and HTTP workers."""
import json
import os
from pathlib import Path
import time
import uuid


class ControlStateExpired(ValueError):
    """The native producer has not refreshed yet; not a malformed record."""


def open_shared(path):
    """Keep reading this generation while a writer replaces its directory entry."""
    if os.name != 'nt':
        return open(path, 'rb')
    import ctypes
    from ctypes import wintypes
    import msvcrt
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    create = kernel.CreateFileW
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                       wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    deadline = time.monotonic() + .25
    while True:
        handle = create(str(Path(path)), 0x80000000, 7, None, 3, 0x80, None)
        if handle != ctypes.c_void_p(-1).value:
            break
        error = ctypes.get_last_error()
        # ReplaceFile can briefly deny a new open, or momentarily leave the
        # name absent. Existing shared handles remain readable throughout.
        if error not in (2, 5, 32, 33) or time.monotonic() >= deadline:
            raise ctypes.WinError(error)
        time.sleep(.005)
    try:
        fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
    except BaseException:
        close = kernel.CloseHandle
        close.argtypes = [wintypes.HANDLE]
        close.restype = wintypes.BOOL
        close(handle)
        raise
    return os.fdopen(fd, 'rb')


def read_json(path, maximum, age=None):
    with open_shared(path) as stream:
        stat = os.fstat(stream.fileno())
        if stat.st_size > maximum:
            raise ValueError('Control state oversized')
        if age is not None and not -0.001 <= time.time() - stat.st_mtime <= age:
            raise ControlStateExpired('Native control state has not refreshed')
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise ValueError('Control state oversized')
    value = json.loads(raw.decode('utf-8-sig'))
    if not isinstance(value, dict):
        raise ValueError('Expected object')
    return value


def atomic(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    pending = path.with_name(path.name + '.' + uuid.uuid4().hex + '.new')
    try:
        pending.write_text(json.dumps(value), encoding='ascii')
        deadline = time.monotonic() + .25
        while True:
            try:
                if os.name == 'nt' and path.exists():
                    import ctypes
                    from ctypes import wintypes
                    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
                    replace = kernel.ReplaceFileW
                    replace.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.LPCWSTR,
                                        wintypes.DWORD, wintypes.LPVOID, wintypes.LPVOID]
                    replace.restype = wintypes.BOOL
                    if not replace(str(path), str(pending), None, 0, None, None):
                        raise ctypes.WinError(ctypes.get_last_error())
                else:
                    os.replace(pending, path)
                break
            except PermissionError as error:
                # A brief native File.ReadAllText (or an old reader) can omit
                # FILE_SHARE_DELETE. Retry on background/HTTP workers only.
                if os.name != 'nt' or error.winerror not in (5, 32, 33) or time.monotonic() >= deadline:
                    raise
                time.sleep(.01)
    finally:
        pending.unlink(missing_ok=True)
