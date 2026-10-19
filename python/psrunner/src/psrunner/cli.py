"""Command line helpers for stage authors.

``python -m psrunner validate <stage-folder>``
    Validates ``stage.json`` and checks that the script declares exactly one ``@stage``.

``python -m psrunner run <stage-folder> --input sample.json [--secret NAME=VALUE ...]``
    Runs the stage locally with the real protocol (separate process, temporary work folder).
    File inputs in the sample are written as ``{"$path": "relative/to/sample.json"}``.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import uuid
from collections.abc import Sequence
from pathlib import Path
from typing import Any

from psrunner.errors import ScriptLoadError
from psrunner.manifest import Manifest, ManifestError, load_manifest
from psrunner.protocol import (
    ENV_INPUT,
    ENV_MEMORY_LIMIT_MB,
    ENV_OUTPUT,
    ENV_PROTOCOL,
    ENV_SCRIPT,
    ENV_SECRET_PREFIX,
    ENV_WORK_DIR,
    PROTOCOL_VERSION,
)
from psrunner.stage import load_stage_function
from psrunner.testing import _copy_input_files


def _validate(folder: Path) -> int:
    try:
        manifest = load_manifest(folder)
        load_stage_function(manifest.script_path)
    except ManifestError as exc:
        print(f"Invalid manifest {exc.path}:", file=sys.stderr)
        for error in exc.errors:
            print(f"  - {error}", file=sys.stderr)
        return 1
    except ScriptLoadError as exc:
        print(f"Invalid script: [{exc.code}] {exc.message}", file=sys.stderr)
        return 1
    print(
        f"OK: {manifest.key}@{manifest.version} "
        f"({len(manifest.inputs)} inputs, {len(manifest.outputs)} outputs)"
    )
    return 0


def _resolve_sample(sample: dict[str, Any], sample_dir: Path, work_dir: Path) -> dict[str, Any]:
    resolved: dict[str, Any] = {}
    for name, value in sample.items():
        if isinstance(value, dict) and "$path" in value:
            resolved[name] = _copy_input_files(name, sample_dir / value["$path"], work_dir)
        elif (
            isinstance(value, list)
            and value
            and all(isinstance(v, dict) and "$path" in v for v in value)
        ):
            resolved[name] = _copy_input_files(
                name, [sample_dir / v["$path"] for v in value], work_dir
            )
        else:
            resolved[name] = value
    return resolved


def _run(folder: Path, input_file: Path | None, secrets: Sequence[str], keep: bool) -> int:
    try:
        manifest: Manifest = load_manifest(folder)
    except ManifestError as exc:
        print(f"Invalid manifest {exc.path}: {'; '.join(exc.errors)}", file=sys.stderr)
        return 1

    work_dir = Path(tempfile.mkdtemp(prefix="psrunner-run-"))
    for sub in ("in", "out", "tmp"):
        (work_dir / sub).mkdir()

    sample: dict[str, Any] = {}
    if input_file is not None:
        sample = json.loads(input_file.read_text(encoding="utf-8"))
    inputs = _resolve_sample(
        sample, (input_file or folder).parent if input_file else folder, work_dir
    )

    document = {
        "protocol": PROTOCOL_VERSION,
        "context": {
            "runId": uuid.uuid4().hex,
            "stepKey": manifest.key,
            "attempt": 1,
            "stage": {"key": manifest.key, "version": manifest.version},
            "isTest": True,
            "inputFields": manifest.raw["inputs"],
            "outputFields": manifest.raw["outputs"],
        },
        "inputs": inputs,
    }
    (work_dir / "input.json").write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")

    env = {key: value for key, value in os.environ.items() if key in {"PATH", "SYSTEMROOT", "LANG"}}
    env.update(
        {
            "TEMP": str(work_dir / "tmp"),
            "TMP": str(work_dir / "tmp"),
            ENV_PROTOCOL: str(PROTOCOL_VERSION),
            ENV_INPUT: str(work_dir / "input.json"),
            ENV_OUTPUT: str(work_dir / "output.json"),
            ENV_WORK_DIR: str(work_dir),
            ENV_SCRIPT: str(manifest.script_path.resolve()),
            ENV_MEMORY_LIMIT_MB: str(manifest.raw.get("memoryLimitMb", 0)),
        }
    )
    for item in secrets:
        name, _, value = item.partition("=")
        env[f"{ENV_SECRET_PREFIX}{name}"] = value

    completed = subprocess.run(
        [sys.executable, "-I", "-X", "utf8", "-m", "psrunner.host"],
        cwd=work_dir,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    if completed.stdout:
        print("--- stdout ---")
        print(completed.stdout.rstrip())
    if completed.stderr:
        print("--- stderr ---")
        print(completed.stderr.rstrip())

    output_path = work_dir / "output.json"
    if not output_path.exists():
        print(
            f"Process exited with code {completed.returncode} without output.json.", file=sys.stderr
        )
        return 1
    output = json.loads(output_path.read_text(encoding="utf-8"))
    print("--- output.json ---")
    print(json.dumps(output, indent=2, ensure_ascii=False))
    if keep:
        print(f"Work folder: {work_dir}")
    else:
        shutil.rmtree(work_dir, ignore_errors=True)
    return 0 if output.get("status") == "ok" else 1


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="psrunner", description="PythonScriptRunner stage tools")
    commands = parser.add_subparsers(dest="command", required=True)

    validate = commands.add_parser("validate", help="validate stage.json and the script")
    validate.add_argument("folder", type=Path)

    run = commands.add_parser("run", help="run a stage locally with the real protocol")
    run.add_argument("folder", type=Path)
    run.add_argument("--input", type=Path, help="JSON file with the inputs")
    run.add_argument("--secret", action="append", default=[], help="NAME=VALUE (repeatable)")
    run.add_argument("--keep", action="store_true", help="print the work folder path")

    args = parser.parse_args(argv)
    if args.command == "validate":
        return _validate(args.folder)
    return _run(args.folder, args.input, args.secret, args.keep)
