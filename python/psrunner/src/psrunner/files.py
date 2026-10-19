"""Access to input and output files of a stage."""

from __future__ import annotations

import builtins
import hashlib
import mimetypes
import os
from pathlib import Path
from typing import IO, Any

_CHUNK = 1024 * 1024


def guess_content_type(name: str) -> str:
    """MIME type guessed from the file name (``application/octet-stream`` when unknown)."""
    content_type, _ = mimetypes.guess_type(name)
    return content_type or "application/octet-stream"


class File:
    """A file received as input or produced as output by a stage."""

    __slots__ = ("_content_type", "_name", "_path", "_sha256", "_size")

    def __init__(
        self,
        path: str | os.PathLike[str],
        name: str | None = None,
        content_type: str | None = None,
        *,
        size: int | None = None,
        sha256: str | None = None,
    ) -> None:
        self._path = Path(path)
        self._name = name or self._path.name
        self._content_type = content_type or guess_content_type(self._name)
        self._size = size
        self._sha256 = sha256

    @property
    def path(self) -> Path:
        return self._path

    @property
    def name(self) -> str:
        return self._name

    @property
    def content_type(self) -> str:
        return self._content_type

    @property
    def size(self) -> int:
        """Size in bytes (read from disk when not provided by the runner)."""
        if self._size is not None:
            return self._size
        return self._path.stat().st_size

    @property
    def sha256(self) -> str:
        """SHA-256 of the content, as lowercase hex."""
        if self._sha256 is not None:
            return self._sha256
        digest = hashlib.sha256()
        with builtins.open(self._path, "rb") as stream:
            for chunk in iter(lambda: stream.read(_CHUNK), b""):
                digest.update(chunk)
        return digest.hexdigest()

    def read_bytes(self) -> bytes:
        return self._path.read_bytes()

    def read_text(self, encoding: str = "utf-8") -> str:
        return self._path.read_text(encoding=encoding)

    def open(self, mode: str = "rb") -> IO[Any]:
        """Opens the file; text modes default to UTF-8."""
        if "b" in mode:
            return builtins.open(self._path, mode)
        return builtins.open(self._path, mode, encoding="utf-8")

    def __repr__(self) -> str:
        return (
            f"File(name={self._name!r}, content_type={self._content_type!r}, "
            f"path={str(self._path)!r})"
        )
