"""Constants shared with the .NET runner (protocol version 1)."""

from __future__ import annotations

PROTOCOL_VERSION = 1

ENV_PROTOCOL = "PSR_PROTOCOL"
ENV_INPUT = "PSR_INPUT"
ENV_OUTPUT = "PSR_OUTPUT"
ENV_WORK_DIR = "PSR_WORK_DIR"
ENV_SCRIPT = "PSR_SCRIPT"
ENV_MEMORY_LIMIT_MB = "PSR_MEMORY_LIMIT_MB"
# Linux: cgroup v2 of the attempt (memory.max) and, if it cannot be joined, the threads of the
# numeric libraries.
ENV_CGROUP = "PSR_CGROUP"
ENV_FALLBACK_THREADS = "PSR_FALLBACK_THREADS"
ENV_SECRET_PREFIX = "PSR_SECRET_"

FILE_MARKER = "$file"

# Error kinds understood by the runner (mirror of the .NET ErrorKind enum).
KIND_INPUT_VALIDATION = "InputValidation"
KIND_OUTPUT_CONTRACT = "OutputContractViolation"
KIND_SCRIPT_ERROR = "ScriptError"
KIND_MEMORY_LIMIT = "MemoryLimitExceeded"
KIND_ENVIRONMENT = "EnvironmentSetupFailed"
