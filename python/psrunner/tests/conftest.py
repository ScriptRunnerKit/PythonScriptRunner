from __future__ import annotations

import json
import os
import subprocess
import sys
import textwrap
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import pytest


@dataclass
class HostRun:
    output: dict[str, Any] | None
    returncode: int
    stdout: str
    stderr: str
    work_dir: Path

    @property
    def outputs(self) -> dict[str, Any]:
        assert self.output is not None, self.stderr
        assert self.output["status"] == "ok", self.output
        return self.output["outputs"]  # type: ignore[no-any-return]

    @property
    def error(self) -> dict[str, Any]:
        assert self.output is not None, self.stderr
        assert self.output["status"] == "error", self.output
        return self.output["error"]  # type: ignore[no-any-return]


RunHost = Callable[..., HostRun]


@pytest.fixture
def run_host(tmp_path: Path) -> RunHost:
    """Runs ``python -I -X utf8 -m psrunner.host`` exactly like the .NET runner does."""

    def _run(
        script: str,
        inputs: dict[str, Any] | None = None,
        input_fields: list[dict[str, Any]] | None = None,
        output_fields: list[dict[str, Any]] | None = None,
        *,
        files: dict[str, tuple[str, bytes, str] | list[tuple[str, bytes, str]]] | None = None,
        secrets: dict[str, str] | None = None,
        protocol: str = "1",
        memory_limit_mb: int = 0,
        extra_env: dict[str, str] | None = None,
    ) -> HostRun:
        work = tmp_path / "work"
        for sub in ("in", "out", "tmp"):
            (work / sub).mkdir(parents=True, exist_ok=True)
        script_path = tmp_path / "script.py"
        script_path.write_text(textwrap.dedent(script), encoding="utf-8")

        wire_inputs = dict(inputs or {})
        for name, given in (files or {}).items():
            items = given if isinstance(given, list) else [given]
            references = []
            for index, (file_name, content, content_type) in enumerate(items):
                relative = Path("in") / name / str(index) / file_name
                (work / relative).parent.mkdir(parents=True, exist_ok=True)
                (work / relative).write_bytes(content)
                references.append(
                    {
                        "$file": {
                            "path": relative.as_posix(),
                            "name": file_name,
                            "contentType": content_type,
                            "size": len(content),
                        }
                    }
                )
            wire_inputs[name] = references if isinstance(given, list) else references[0]

        document = {
            "protocol": 1,
            "context": {
                "runId": "run-1",
                "stepKey": "step",
                "attempt": 1,
                "stage": {"key": "test-stage", "version": 1},
                "isTest": False,
                "inputFields": input_fields or [],
                "outputFields": output_fields or [],
            },
            "inputs": wire_inputs,
        }
        (work / "input.json").write_text(json.dumps(document), encoding="utf-8")

        env = {"PATH": os.environ.get("PATH", "")}
        if "SYSTEMROOT" in os.environ:
            env["SYSTEMROOT"] = os.environ["SYSTEMROOT"]
        env.update(
            {
                "TEMP": str(work / "tmp"),
                "TMP": str(work / "tmp"),
                "PSR_PROTOCOL": protocol,
                "PSR_INPUT": str(work / "input.json"),
                "PSR_OUTPUT": str(work / "output.json"),
                "PSR_WORK_DIR": str(work),
                "PSR_SCRIPT": str(script_path),
                "PSR_MEMORY_LIMIT_MB": str(memory_limit_mb),
            }
        )
        for name, value in (secrets or {}).items():
            env[f"PSR_SECRET_{name}"] = value
        env.update(extra_env or {})

        completed = subprocess.run(
            [sys.executable, "-I", "-X", "utf8", "-m", "psrunner.host"],
            cwd=work,
            env=env,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=120,
            check=False,
        )
        output_path = work / "output.json"
        output = (
            json.loads(output_path.read_text(encoding="utf-8")) if output_path.exists() else None
        )
        return HostRun(output, completed.returncode, completed.stdout, completed.stderr, work)

    return _run
