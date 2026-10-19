"""Execution context handed to the stage function."""

from __future__ import annotations

import logging
from collections.abc import Iterator, Mapping
from pathlib import Path

from psrunner.files import File


class _Secrets(Mapping[str, str]):
    """Read-only view of the secrets declared by the stage."""

    def __init__(self, values: Mapping[str, str]) -> None:
        self._values = dict(values)

    def __getitem__(self, key: str) -> str:
        try:
            return self._values[key]
        except KeyError:
            declared = ", ".join(sorted(self._values)) or "(none)"
            raise KeyError(
                f"Secret '{key}' was not declared by this stage. Declared secrets: {declared}."
            ) from None

    def __iter__(self) -> Iterator[str]:
        return iter(self._values)

    def __len__(self) -> int:
        return len(self._values)

    def __repr__(self) -> str:
        return f"<secrets: {', '.join(sorted(self._values)) or 'none'}>"


class Context:
    """Metadata and helpers available to the stage while it runs."""

    def __init__(
        self,
        *,
        work_dir: Path,
        run_id: str = "",
        step_key: str = "",
        attempt: int = 1,
        stage_key: str = "",
        stage_version: int = 0,
        is_test: bool = False,
        secrets: Mapping[str, str] | None = None,
        logger: logging.Logger | None = None,
    ) -> None:
        self._work_dir = work_dir
        self._run_id = run_id
        self._step_key = step_key
        self._attempt = attempt
        self._stage_key = stage_key
        self._stage_version = stage_version
        self._is_test = is_test
        self._secrets = _Secrets(secrets or {})
        self._log = logger or logging.getLogger("psrunner.stage")

    @property
    def run_id(self) -> str:
        return self._run_id

    @property
    def step_key(self) -> str:
        return self._step_key

    @property
    def attempt(self) -> int:
        return self._attempt

    @property
    def stage_key(self) -> str:
        return self._stage_key

    @property
    def stage_version(self) -> int:
        return self._stage_version

    @property
    def is_test(self) -> bool:
        return self._is_test

    @property
    def work_dir(self) -> Path:
        return self._work_dir

    @property
    def out_dir(self) -> Path:
        return self._work_dir / "out"

    @property
    def tmp_dir(self) -> Path:
        return self._work_dir / "tmp"

    @property
    def log(self) -> logging.Logger:
        return self._log

    @property
    def secrets(self) -> Mapping[str, str]:
        return self._secrets

    def new_file(self, name: str, content_type: str | None = None) -> File:
        """Reserves an output file in ``out/``; the script writes to ``File.path``."""
        if (
            not name
            or name in {".", ".."}
            or "/" in name
            or "\\" in name
            or Path(name).name != name
        ):
            raise ValueError(f"Invalid output file name: {name!r}. Use a plain file name.")
        self.out_dir.mkdir(parents=True, exist_ok=True)
        path = self.out_dir / name
        if path.exists():
            raise FileExistsError(f"Output file '{name}' already exists.")
        return File(path, name, content_type)
