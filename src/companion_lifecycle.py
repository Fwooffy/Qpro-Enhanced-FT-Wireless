"""Bind a camera session to the exact Windows Companion process that started it."""

from __future__ import annotations

import ctypes
import os
from ctypes import wintypes


class _WindowsProcessApi:
    SYNCHRONIZE = 0x00100000
    QUERY_LIMITED_INFORMATION = 0x1000
    WAIT_OBJECT_0 = 0
    WAIT_TIMEOUT = 0x102

    def __init__(self) -> None:
        if os.name != "nt":
            raise OSError("Companion-owned camera sessions require Windows")
        self._kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self._kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self._kernel.OpenProcess.restype = wintypes.HANDLE
        self._kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        self._kernel.GetProcessTimes.restype = wintypes.BOOL
        self._kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self._kernel.WaitForSingleObject.restype = wintypes.DWORD
        self._kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self._kernel.CloseHandle.restype = wintypes.BOOL

    def open(self, pid: int) -> object | None:
        handle = self._kernel.OpenProcess(self.SYNCHRONIZE | self.QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            error = ctypes.get_last_error()
            if error == 87:  # The owner already exited before the receiver opened it.
                return None
            raise ctypes.WinError(error)
        return handle

    def creation_filetime(self, handle: object) -> int:
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not self._kernel.GetProcessTimes(handle, *(ctypes.byref(value) for value in (created, exited, kernel, user))):
            raise ctypes.WinError(ctypes.get_last_error())
        return (created.dwHighDateTime << 32) | created.dwLowDateTime

    def alive(self, handle: object) -> bool:
        result = self._kernel.WaitForSingleObject(handle, 0)
        if result == self.WAIT_TIMEOUT:
            return True
        if result == self.WAIT_OBJECT_0:
            return False
        raise ctypes.WinError(ctypes.get_last_error())

    def close(self, handle: object) -> None:
        self._kernel.CloseHandle(handle)


class CompanionLifetime:
    """Hold one process handle; a recycled PID must not keep an orphan alive.

    Omitting both identifiers preserves standalone capture/CLI behavior. With
    identifiers supplied, inability to verify the owner fails closed before
    camera output starts. The handle remains bound to that process after exit.
    """

    def __init__(self, pid: int | None = None, start_filetime: int | None = None, *, _api=None) -> None:
        self._handle = None
        self._api = None
        self.enabled = pid is not None or start_filetime is not None
        if not self.enabled:
            return
        if pid is None or start_filetime is None or not 0 < pid <= 0xFFFFFFFF or not 0 < start_filetime <= 0x7FFFFFFFFFFFFFFF:
            raise ValueError("Companion PID and creation FILETIME must both be positive integers")
        self._api = _api if _api is not None else _WindowsProcessApi()
        handle = self._api.open(pid)
        if handle is None:
            return
        try:
            if self._api.creation_filetime(handle) != start_filetime:
                raise OSError("Companion process identity changed before camera startup")
        except BaseException:
            self._api.close(handle)
            raise
        self._handle = handle

    def alive(self) -> bool:
        if not self.enabled:
            return True
        return self._handle is not None and self._api.alive(self._handle)

    def close(self) -> None:
        if self._handle is not None:
            handle, self._handle = self._handle, None
            self._api.close(handle)
