"""Shared helpers to call the stage function and classify failures."""

from __future__ import annotations

import asyncio
import inspect
import traceback
from collections.abc import Callable
from pathlib import Path
from typing import Any

from psrunner.context import Context
from psrunner.errors import ContractError, ScriptLoadError, StageError
from psrunner.protocol import KIND_MEMORY_LIMIT, KIND_SCRIPT_ERROR

_PACKAGE_DIR = str(Path(__file__).resolve().parent)


def call_stage(fn: Callable[..., Any], inputs: dict[str, Any], ctx: Context) -> Any:
    """Calls ``fn(inputs, ctx)``; coroutines (``async def run``) are awaited."""
    result = fn(inputs, ctx)
    if inspect.iscoroutine(result):
        result = asyncio.run(result)
    return result


def _is_internal(filename: str) -> bool:
    if filename.startswith("<frozen importlib"):
        return True
    try:
        return str(Path(filename).resolve()).startswith(_PACKAGE_DIR)
    except OSError:
        return False


def user_traceback(exc: BaseException) -> str:
    """Traceback without the SDK's own frames, so it points at the user script."""
    frames = [f for f in traceback.extract_tb(exc.__traceback__) if not _is_internal(f.filename)]
    lines = ["Traceback (most recent call last):\n"]
    lines += traceback.format_list(frames)
    lines += traceback.format_exception_only(type(exc), exc)
    return "".join(lines)


def classify_exception(exc: BaseException) -> dict[str, Any]:
    """Maps an exception to the protocol error object."""
    if isinstance(exc, StageError):
        error: dict[str, Any] = {
            "kind": KIND_SCRIPT_ERROR,
            "code": exc.code,
            "message": exc.message,
            "traceback": user_traceback(exc),
        }
        if exc.details is not None:
            error["details"] = exc.details
        return error
    if isinstance(exc, ContractError):
        return {"kind": exc.kind, "code": exc.code, "message": exc.message}
    if isinstance(exc, ScriptLoadError):
        error = {"kind": KIND_SCRIPT_ERROR, "code": exc.code, "message": exc.message}
        if exc.__cause__ is not None and not isinstance(exc.__cause__, SyntaxError):
            error["traceback"] = user_traceback(exc.__cause__)
        return error
    if isinstance(exc, MemoryError):
        return {
            "kind": KIND_MEMORY_LIMIT,
            "code": "MEMORY_LIMIT_EXCEEDED",
            "message": "The script exceeded its memory limit.",
        }
    if isinstance(exc, SystemExit):
        return {
            "kind": KIND_SCRIPT_ERROR,
            "code": "SCRIPT_EXITED",
            "message": (
                f"The script called sys.exit({exc.code!r}); return a dict or raise StageError."
            ),
        }
    if isinstance(exc, KeyboardInterrupt):
        return {
            "kind": KIND_SCRIPT_ERROR,
            "code": "INTERRUPTED",
            "message": "The script was interrupted.",
        }
    return {
        "kind": KIND_SCRIPT_ERROR,
        "code": None,
        "message": f"{type(exc).__name__}: {exc}",
        "traceback": user_traceback(exc),
    }
