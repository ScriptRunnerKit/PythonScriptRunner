"""Errors raised by stage scripts and by the SDK."""

from __future__ import annotations

from typing import Any


class StageError(Exception):
    """Business error raised by a stage script.

    The runner reports it as ``ScriptError`` with the given ``code`` so callers can react to it.
    """

    def __init__(
        self, message: str, *, code: str | None = None, details: dict[str, Any] | None = None
    ) -> None:
        super().__init__(message)
        self.message = message
        self.code = code
        self.details = details


class ContractError(Exception):
    """Inputs or outputs do not match the declared fields."""

    def __init__(self, kind: str, code: str, message: str) -> None:
        super().__init__(message)
        self.kind = kind
        self.code = code
        self.message = message


class ScriptLoadError(Exception):
    """The script could not be loaded or does not declare exactly one ``@stage`` function."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
