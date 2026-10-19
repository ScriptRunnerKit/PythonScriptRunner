"""Run a stage in-process (no .NET needed), e.g. from pytest.

Example::

    from psrunner.testing import run_stage

    def test_terms():
        result = run_stage("stages/terms/script.py", {"a1": 1, "ratio": 2, "n": 4},
                           stage_json="stages/terms/stage.json")
        assert result.ok
        assert result.outputs["terms"] == [1, 2, 4, 8]
"""

from __future__ import annotations

import logging
import os
import shutil
import tempfile
import uuid
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from psrunner._execution import call_stage, classify_exception
from psrunner.context import Context
from psrunner.convert import inputs_to_python, outputs_to_wire, python_to_wire
from psrunner.errors import ContractError
from psrunner.fields import FieldSpec
from psrunner.files import File, guess_content_type
from psrunner.manifest import load_manifest
from psrunner.protocol import FILE_MARKER, KIND_INPUT_VALIDATION
from psrunner.stage import load_stage_function

PathLike = str | os.PathLike[str]


@dataclass(frozen=True)
class StageRunError:
    """Failure reported by :func:`run_stage` (same shape as the protocol error)."""

    kind: str
    code: str | None
    message: str
    traceback: str | None = None


@dataclass
class StageRunResult:
    """Outcome of :func:`run_stage`."""

    outputs: dict[str, Any] | None
    """Values exactly as returned by ``run()``."""
    wire_outputs: dict[str, Any] | None
    """Outputs converted to the wire format (only when a contract was given)."""
    error: StageRunError | None
    logs: list[str] = field(default_factory=list)
    work_dir: Path = field(default_factory=Path)

    @property
    def ok(self) -> bool:
        return self.error is None


class _ListHandler(logging.Handler):
    def __init__(self, sink: list[str]) -> None:
        super().__init__()
        self._sink = sink
        self.setFormatter(logging.Formatter("%(levelname)s %(message)s"))

    def emit(self, record: logging.LogRecord) -> None:
        self._sink.append(self.format(record))


def _copy_input_files(
    name: str, paths: PathLike | Sequence[PathLike], work_dir: Path
) -> dict[str, Any] | list[dict[str, Any]]:
    """Copies files into ``in/<field>/<index>/`` and returns wire references."""
    many = not isinstance(paths, (str, os.PathLike))
    items: Sequence[PathLike] = [paths] if isinstance(paths, (str, os.PathLike)) else paths
    references: list[dict[str, Any]] = []
    for index, source in enumerate(items):
        source_path = Path(source)
        relative = Path("in") / name / str(index) / source_path.name
        target = work_dir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source_path, target)
        reference = File(target, source_path.name, guess_content_type(source_path.name))
        references.append(
            {
                FILE_MARKER: {
                    "path": relative.as_posix(),
                    "name": reference.name,
                    "contentType": reference.content_type,
                    "size": reference.size,
                    "sha256": reference.sha256,
                }
            }
        )
    return references if many else references[0]


def _file_from_reference(reference: Mapping[str, Any], work_dir: Path) -> File:
    info = reference[FILE_MARKER]
    return File(
        work_dir / info["path"],
        info["name"],
        info["contentType"],
        size=info["size"],
        sha256=info["sha256"],
    )


def _inputs_to_wire(
    inputs: Mapping[str, Any], specs: Sequence[FieldSpec], work_dir: Path
) -> dict[str, Any]:
    by_name = {spec.name: spec for spec in specs}
    unknown = sorted(name for name in inputs if name not in by_name)
    if unknown:
        raise ContractError(
            KIND_INPUT_VALIDATION,
            "UNDECLARED_INPUT",
            f"Inputs not declared by the stage: {', '.join(unknown)}.",
        )
    wire: dict[str, Any] = {}
    for name, value in inputs.items():
        spec = by_name[name]
        if value is None or spec.is_file:
            wire[name] = value
        else:
            wire[name] = python_to_wire(value, spec, work_dir, name, KIND_INPUT_VALIDATION)
    return wire


def run_stage(
    script: PathLike | Callable[..., Any],
    inputs: Mapping[str, Any] | None = None,
    *,
    stage_json: PathLike | None = None,
    secrets: Mapping[str, str] | None = None,
    files: Mapping[str, PathLike | Sequence[PathLike]] | None = None,
    work_dir: PathLike | None = None,
    run_id: str | None = None,
) -> StageRunResult:
    """Runs one stage function with the same conversions and validations as the real runner.

    ``files`` maps file fields to local paths (a list for ``list<file>``). When ``stage_json``
    is given, inputs and outputs are validated against the declared fields; otherwise inputs
    are passed through as given.
    """
    directory = (
        Path(work_dir) if work_dir is not None else Path(tempfile.mkdtemp(prefix="psrunner-"))
    )
    for sub in ("in", "out", "tmp"):
        (directory / sub).mkdir(parents=True, exist_ok=True)

    logs: list[str] = []
    logger = logging.getLogger(f"psrunner.stage.test.{uuid.uuid4().hex}")
    logger.setLevel(logging.DEBUG)
    logger.propagate = False
    logger.addHandler(_ListHandler(logs))

    manifest = load_manifest(stage_json) if stage_json is not None else None
    ctx = Context(
        work_dir=directory,
        run_id=run_id or uuid.uuid4().hex,
        step_key=manifest.key if manifest else "test",
        stage_key=manifest.key if manifest else "test",
        stage_version=manifest.version if manifest else 0,
        is_test=True,
        secrets=secrets,
        logger=logger,
    )

    result: Any = None
    try:
        raw: dict[str, Any] = dict(inputs or {})
        file_refs = {
            name: _copy_input_files(name, paths, directory) for name, paths in (files or {}).items()
        }
        if manifest is not None:
            wire = _inputs_to_wire(raw, manifest.inputs, directory)
            wire.update(file_refs)
            converted = inputs_to_python(wire, manifest.inputs, directory)
        else:
            converted = raw
            for name, reference in file_refs.items():
                if isinstance(reference, list):
                    converted[name] = [_file_from_reference(r, directory) for r in reference]
                else:
                    converted[name] = _file_from_reference(reference, directory)

        fn = script if callable(script) else load_stage_function(Path(script))
        result = call_stage(fn, converted, ctx)
        wire_outputs = (
            outputs_to_wire(result, manifest.outputs, directory) if manifest is not None else None
        )
    except BaseException as exc:
        if isinstance(exc, KeyboardInterrupt):
            raise
        info = classify_exception(exc)
        return StageRunResult(
            outputs=result if isinstance(result, dict) else None,
            wire_outputs=None,
            error=StageRunError(
                info["kind"], info.get("code"), info["message"], info.get("traceback")
            ),
            logs=logs,
            work_dir=directory,
        )

    return StageRunResult(
        outputs=dict(result) if isinstance(result, Mapping) else None,
        wire_outputs=wire_outputs,
        error=None,
        logs=logs,
        work_dir=directory,
    )
