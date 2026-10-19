"""Process entry point used by the .NET runner: ``python -I -X utf8 -m psrunner.host``.

Reads ``input.json``, runs the ``@stage`` function and always writes ``output.json``
(exit code 0) unless the output itself cannot be written.
"""

from __future__ import annotations

import json
import logging
import os
import sys
from pathlib import Path
from typing import Any

from psrunner._execution import call_stage, classify_exception
from psrunner.context import Context
from psrunner.convert import inputs_to_python, outputs_to_wire
from psrunner.fields import specs_from_dicts
from psrunner.limits import apply_memory_limit
from psrunner.protocol import (
    ENV_INPUT,
    ENV_MEMORY_LIMIT_MB,
    ENV_OUTPUT,
    ENV_PROTOCOL,
    ENV_SCRIPT,
    ENV_SECRET_PREFIX,
    ENV_WORK_DIR,
    KIND_ENVIRONMENT,
    PROTOCOL_VERSION,
)
from psrunner.stage import load_stage_function


def _configure_logging() -> logging.Logger:
    logger = logging.getLogger("psrunner.stage")
    handler = logging.StreamHandler(sys.stderr)
    handler.setFormatter(logging.Formatter("%(levelname)s %(message)s"))
    logger.handlers[:] = [handler]
    logger.setLevel(logging.INFO)
    logger.propagate = False
    return logger


def _secrets_from_env() -> dict[str, str]:
    return {
        name[len(ENV_SECRET_PREFIX) :]: value
        for name, value in os.environ.items()
        if name.startswith(ENV_SECRET_PREFIX)
    }


def _protocol_error(message: str) -> dict[str, Any]:
    return {
        "protocol": PROTOCOL_VERSION,
        "status": "error",
        "error": {"kind": KIND_ENVIRONMENT, "code": "PROTOCOL_MISMATCH", "message": message},
    }


def _run() -> dict[str, Any]:
    requested = os.environ.get(ENV_PROTOCOL, "")
    if requested != str(PROTOCOL_VERSION):
        return _protocol_error(
            f"Runner requested protocol {requested or '(none)'}, "
            f"this psrunner speaks {PROTOCOL_VERSION}."
        )

    apply_memory_limit(int(os.environ.get(ENV_MEMORY_LIMIT_MB, "0") or 0))
    logger = _configure_logging()

    try:
        document = json.loads(Path(os.environ[ENV_INPUT]).read_text(encoding="utf-8"))
        if document.get("protocol") != PROTOCOL_VERSION:
            return _protocol_error(f"input.json uses protocol {document.get('protocol')!r}.")

        info = document.get("context") or {}
        stage_info = info.get("stage") or {}
        work_dir = Path(os.environ[ENV_WORK_DIR])
        ctx = Context(
            work_dir=work_dir,
            run_id=str(info.get("runId", "")),
            step_key=str(info.get("stepKey", "")),
            attempt=int(info.get("attempt", 1)),
            stage_key=str(stage_info.get("key", "")),
            stage_version=int(stage_info.get("version", 0)),
            is_test=bool(info.get("isTest", False)),
            secrets=_secrets_from_env(),
            logger=logger,
        )
        input_specs = specs_from_dicts(info.get("inputFields"))
        output_specs = specs_from_dicts(info.get("outputFields"))

        inputs = inputs_to_python(document.get("inputs") or {}, input_specs, work_dir)
        fn = load_stage_function(Path(os.environ[ENV_SCRIPT]))
        result = call_stage(fn, inputs, ctx)
        outputs = outputs_to_wire(result, output_specs, work_dir)
    except BaseException as exc:  # every failure is reported through output.json
        return {"protocol": PROTOCOL_VERSION, "status": "error", "error": classify_exception(exc)}

    return {"protocol": PROTOCOL_VERSION, "status": "ok", "outputs": outputs}


def main() -> int:
    output_path = os.environ.get(ENV_OUTPUT)
    if not output_path:
        print(
            f"psrunner: {ENV_OUTPUT} is not set; this module is started by the .NET runner.",
            file=sys.stderr,
        )
        return 2

    document = _run()
    try:
        Path(output_path).write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")
    except (OSError, TypeError, ValueError) as exc:
        print(f"psrunner: could not write output.json: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
