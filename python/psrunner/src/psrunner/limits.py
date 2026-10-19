"""Resource limits applied by the interpreter to itself, before the user script is imported.

The limit is applied here, inside the real interpreter process, because on Windows the venv
``python.exe`` is a launcher that starts the interpreter as a child process: a Job Object assigned
by the .NET runner after ``Process.Start`` can miss that child.
"""

from __future__ import annotations

import sys
from typing import Any

_MB = 1024 * 1024
_keep_alive: list[Any] = []


def apply_memory_limit(limit_mb: int) -> bool:
    """Limits the memory of this process. Returns ``True`` when a limit was applied."""
    if limit_mb <= 0:
        return False
    if sys.platform == "win32":
        return _limit_windows(limit_mb * _MB)
    else:
        return _limit_posix(limit_mb * _MB)


if sys.platform == "win32":
    import ctypes
    from ctypes import wintypes

    _JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9
    _JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100

    class _BasicLimits(ctypes.Structure):
        _fields_ = [
            ("PerProcessUserTimeLimit", ctypes.c_int64),
            ("PerJobUserTimeLimit", ctypes.c_int64),
            ("LimitFlags", wintypes.DWORD),
            ("MinimumWorkingSetSize", ctypes.c_size_t),
            ("MaximumWorkingSetSize", ctypes.c_size_t),
            ("ActiveProcessLimit", wintypes.DWORD),
            ("Affinity", ctypes.c_size_t),
            ("PriorityClass", wintypes.DWORD),
            ("SchedulingClass", wintypes.DWORD),
        ]

    class _IoCounters(ctypes.Structure):
        _fields_ = [
            ("ReadOperationCount", ctypes.c_ulonglong),
            ("WriteOperationCount", ctypes.c_ulonglong),
            ("OtherOperationCount", ctypes.c_ulonglong),
            ("ReadTransferCount", ctypes.c_ulonglong),
            ("WriteTransferCount", ctypes.c_ulonglong),
            ("OtherTransferCount", ctypes.c_ulonglong),
        ]

    class _ExtendedLimits(ctypes.Structure):
        _fields_ = [
            ("BasicLimitInformation", _BasicLimits),
            ("IoInfo", _IoCounters),
            ("ProcessMemoryLimit", ctypes.c_size_t),
            ("JobMemoryLimit", ctypes.c_size_t),
            ("PeakProcessMemoryUsed", ctypes.c_size_t),
            ("PeakJobMemoryUsed", ctypes.c_size_t),
        ]

    def _limit_windows(limit: int) -> bool:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        kernel32.SetInformationJobObject.argtypes = [
            wintypes.HANDLE,
            ctypes.c_int,
            ctypes.c_void_p,
            wintypes.DWORD,
        ]
        kernel32.SetInformationJobObject.restype = wintypes.BOOL
        kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
        kernel32.GetCurrentProcess.restype = wintypes.HANDLE

        job = kernel32.CreateJobObjectW(None, None)
        if not job:
            print(f"psrunner: CreateJobObject failed ({ctypes.get_last_error()})", file=sys.stderr)
            return False

        info = _ExtendedLimits()
        info.BasicLimitInformation.LimitFlags = _JOB_OBJECT_LIMIT_PROCESS_MEMORY
        info.ProcessMemoryLimit = limit
        applied = kernel32.SetInformationJobObject(
            job, _JOB_OBJECT_EXTENDED_LIMIT_INFORMATION, ctypes.byref(info), ctypes.sizeof(info)
        ) and kernel32.AssignProcessToJobObject(job, kernel32.GetCurrentProcess())
        if not applied:
            print(
                f"psrunner: could not apply memory limit ({ctypes.get_last_error()})",
                file=sys.stderr,
            )
            return False
        _keep_alive.append(job)
        return True

else:
    import resource

    def _limit_posix(limit: int) -> bool:
        try:
            resource.setrlimit(resource.RLIMIT_AS, (limit, limit))
        except (ValueError, OSError) as exc:
            print(f"psrunner: could not apply memory limit: {exc}", file=sys.stderr)
            return False
        return True
