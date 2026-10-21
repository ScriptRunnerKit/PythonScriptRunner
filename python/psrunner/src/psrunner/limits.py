"""Resource limits applied by the interpreter to itself, before the user script is imported.

The limit is applied here, inside the real interpreter process, because on Windows the venv
``python.exe`` is a launcher that starts the interpreter as a child process: a Job Object assigned
by the .NET runner after ``Process.Start`` can miss that child.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Any

_MB = 1024 * 1024
_keep_alive: list[Any] = []

# Thread pools of the numeric libraries (OpenMP, OpenBLAS, MKL, numexpr, Accelerate).
THREAD_VARIABLES = (
    "OMP_NUM_THREADS",
    "OPENBLAS_NUM_THREADS",
    "MKL_NUM_THREADS",
    "NUMEXPR_NUM_THREADS",
    "VECLIB_MAXIMUM_THREADS",
)


def apply_limits(limit_mb: int, cgroup: str, fallback_threads: int) -> str:
    """Applies the memory limit of the attempt; returns how: ``cgroup``, ``process`` or ``none``.

    On Linux the runner may create a cgroup v2 with ``memory.max`` for the attempt (spec 001,
    D-180): joining it limits the memory really used. Without it, the limit falls back to
    ``RLIMIT_AS``, which counts reserved address space, so the numeric libraries are limited to
    ``fallback_threads`` threads: OpenBLAS reserves memory per thread and, on a machine with many
    cores, ``import numpy`` alone would exceed the limit. Both happen before the script is imported.
    """
    if cgroup and join_cgroup(cgroup):
        return "cgroup"
    if fallback_threads > 0:
        limit_threads(fallback_threads)
    return "process" if apply_memory_limit(limit_mb) else "none"


def join_cgroup(path: str) -> bool:
    """Moves this process into the cgroup at ``path``. Returns ``True`` when it joined."""
    try:
        Path(path, "cgroup.procs").write_text(str(os.getpid()), encoding="ascii")
    except OSError as exc:
        print(f"psrunner: could not join the cgroup {path}: {exc}", file=sys.stderr)
        return False
    return True


def limit_threads(count: int) -> None:
    """Limits the thread pools of the numeric libraries, keeping values set by the runner."""
    for name in THREAD_VARIABLES:
        os.environ.setdefault(name, str(count))


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
