"""Resource limits applied before the user script is imported."""

from __future__ import annotations

import sys


def apply_memory_limit(limit_mb: int) -> bool:
    """Limits the address space of this process (POSIX only).

    On Windows the .NET runner enforces the limit with a Job Object, so this is a no-op.
    Returns ``True`` when a limit was applied.
    """
    if limit_mb <= 0:
        return False
    if sys.platform == "win32":
        return False

    import resource

    limit = limit_mb * 1024 * 1024
    try:
        resource.setrlimit(resource.RLIMIT_AS, (limit, limit))
    except (ValueError, OSError) as exc:
        print(f"psrunner: could not apply memory limit: {exc}", file=sys.stderr)
        return False
    return True
