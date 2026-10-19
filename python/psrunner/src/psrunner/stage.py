"""The ``@stage`` decorator and script loading."""

from __future__ import annotations

import hashlib
import importlib.util
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any, TypeVar

from psrunner.errors import ScriptLoadError

F = TypeVar("F", bound=Callable[..., Any])

_registry: list[Callable[..., Any]] = []


def stage(fn: F) -> F:
    """Marks ``fn(inputs, ctx)`` as the entry point of the script (exactly one per script)."""
    _registry.append(fn)
    return fn


def load_stage_function(script: Path) -> Callable[..., Any]:
    """Imports the script and returns its single ``@stage`` function."""
    if not script.is_file():
        raise ScriptLoadError("SCRIPT_NOT_FOUND", f"Script not found: {script}")

    _registry.clear()
    digest = hashlib.sha256(str(script.resolve()).encode()).hexdigest()[:16]
    module_name = f"psrunner_stage_{digest}"
    spec = importlib.util.spec_from_file_location(module_name, script)
    if spec is None or spec.loader is None:
        raise ScriptLoadError("SCRIPT_NOT_FOUND", f"Cannot load script: {script}")

    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    try:
        spec.loader.exec_module(module)
    except SyntaxError as exc:
        raise ScriptLoadError(
            "SYNTAX_ERROR", f"{exc.msg} (line {exc.lineno}, column {exc.offset})"
        ) from exc

    found = list(_registry)
    if not found:
        raise ScriptLoadError(
            "NO_STAGE_FUNCTION", "The script does not define a function decorated with @stage."
        )
    if len(found) > 1:
        names = ", ".join(fn.__name__ for fn in found)
        raise ScriptLoadError(
            "MULTIPLE_STAGE_FUNCTIONS",
            f"The script defines {len(found)} functions decorated with @stage ({names}); "
            "exactly one is allowed.",
        )
    return found[0]
