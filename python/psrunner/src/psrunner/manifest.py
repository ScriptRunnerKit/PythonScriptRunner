"""Loading and validation of ``stage.json`` (same rules as ``schemas/stage.v1.json``)."""

from __future__ import annotations

import json
import os
import re
from collections.abc import Mapping
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from psrunner.convert import wire_to_python
from psrunner.errors import ContractError
from psrunner.fields import ALL_TYPES, FIELD_NAME, LIST_ITEM_TYPES, SCALAR_TYPES, FieldSpec

_KEY = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
_PYTHON = re.compile(r"^3\.(1[0-9]|[2-9][0-9])$")
_SECRET = re.compile(r"^[A-Z][A-Z0-9_]{0,63}$")
_TOP_LEVEL = {
    "$schema",
    "key",
    "version",
    "name",
    "description",
    "python",
    "script",
    "requirements",
    "timeoutSeconds",
    "memoryLimitMb",
    "retry",
    "idempotent",
    "secrets",
    "inputs",
    "outputs",
}
_FIELD_KEYS = {
    "name",
    "type",
    "itemType",
    "required",
    "default",
    "description",
    "enumValues",
    "contentTypes",
    "columns",
}


class ManifestError(Exception):
    """``stage.json`` is invalid; ``errors`` lists every problem found."""

    def __init__(self, path: Path, errors: list[str]) -> None:
        super().__init__(f"{path}: " + "; ".join(errors))
        self.path = path
        self.errors = errors


@dataclass(frozen=True)
class Manifest:
    """A loaded and validated stage manifest."""

    folder: Path
    key: str
    version: int
    name: str
    python: str
    script: str
    requirements: str
    inputs: list[FieldSpec]
    outputs: list[FieldSpec]
    raw: dict[str, Any] = field(repr=False)

    @property
    def script_path(self) -> Path:
        return self.folder / self.script

    @property
    def secrets(self) -> list[str]:
        return list(self.raw.get("secrets") or [])


def load_manifest(path: str | os.PathLike[str]) -> Manifest:
    """Reads ``stage.json`` (or the folder that contains it) and validates it."""
    manifest_path = Path(path)
    if manifest_path.is_dir():
        manifest_path = manifest_path / "stage.json"
    try:
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ManifestError(manifest_path, [f"cannot read manifest: {exc}"]) from exc

    errors = validate_manifest_data(data)
    if errors:
        raise ManifestError(manifest_path, errors)
    return Manifest(
        folder=manifest_path.parent,
        key=data["key"],
        version=data["version"],
        name=data["name"],
        python=data.get("python", "3.12"),
        script=data.get("script", "script.py"),
        requirements=data.get("requirements", "requirements.txt"),
        inputs=[FieldSpec.from_dict(f) for f in data["inputs"]],
        outputs=[FieldSpec.from_dict(f) for f in data["outputs"]],
        raw=data,
    )


def validate_manifest_data(data: Any) -> list[str]:
    """Returns every validation error of a manifest document (empty when valid)."""
    if not isinstance(data, Mapping):
        return ["manifest must be a JSON object"]
    errors: list[str] = [f"unknown property '{k}'" for k in data if k not in _TOP_LEVEL]

    for required in ("key", "version", "name", "inputs", "outputs"):
        if required not in data:
            errors.append(f"missing required property '{required}'")

    key = data.get("key")
    if key is not None and (
        not isinstance(key, str) or not 3 <= len(key) <= 64 or not _KEY.match(key)
    ):
        errors.append("'key' must be a slug of 3-64 chars (a-z, 0-9, '-')")
    version = data.get("version")
    if version is not None and (
        isinstance(version, bool) or not isinstance(version, int) or version < 1
    ):
        errors.append("'version' must be an integer >= 1")
    name = data.get("name")
    if name is not None and (not isinstance(name, str) or not 1 <= len(name) <= 200):
        errors.append("'name' must be a string of 1-200 chars")
    python = data.get("python")
    if python is not None and (not isinstance(python, str) or not _PYTHON.match(python)):
        errors.append("'python' must look like '3.12' (minimum 3.10)")
    _check_int(data, "timeoutSeconds", 1, 86400, errors)
    _check_int(data, "memoryLimitMb", 64, None, errors)
    _check_retry(data.get("retry"), errors)
    if "idempotent" in data and not isinstance(data["idempotent"], bool):
        errors.append("'idempotent' must be a boolean")
    secrets = data.get("secrets")
    if secrets is not None:
        if not isinstance(secrets, list) or len(set(map(str, secrets))) != len(secrets):
            errors.append("'secrets' must be a list of unique names")
        else:
            errors += [
                f"invalid secret name '{s}'"
                for s in secrets
                if not isinstance(s, str) or not _SECRET.match(s)
            ]

    for section in ("inputs", "outputs"):
        fields = data.get(section)
        if fields is None:
            continue
        if not isinstance(fields, list):
            errors.append(f"'{section}' must be a list")
            continue
        names: set[str] = set()
        for index, item in enumerate(fields):
            errors += [f"{section}[{index}]: {e}" for e in validate_field(item)]
            field_name = item.get("name") if isinstance(item, Mapping) else None
            if isinstance(field_name, str):
                if field_name in names:
                    errors.append(f"{section}[{index}]: duplicate field name '{field_name}'")
                names.add(field_name)
    return errors


def _check_int(
    data: Mapping[str, Any], key: str, minimum: int, maximum: int | None, errors: list[str]
) -> None:
    if key not in data:
        return
    value = data[key]
    if (
        isinstance(value, bool)
        or not isinstance(value, int)
        or value < minimum
        or (maximum is not None and value > maximum)
    ):
        bounds = f">= {minimum}" if maximum is None else f"between {minimum} and {maximum}"
        errors.append(f"'{key}' must be an integer {bounds}")


def _check_retry(retry: Any, errors: list[str]) -> None:
    if retry is None:
        return
    if not isinstance(retry, Mapping):
        errors.append("'retry' must be an object")
        return
    errors += [
        f"retry: unknown property '{k}'"
        for k in retry
        if k not in {"maxAttempts", "initialDelaySeconds", "backoffMultiplier"}
    ]
    _check_int(retry, "maxAttempts", 0, 20, errors)
    _check_int(retry, "initialDelaySeconds", 0, 600, errors)
    multiplier = retry.get("backoffMultiplier")
    if multiplier is not None and (
        isinstance(multiplier, bool)
        or not isinstance(multiplier, (int, float))
        or not 1 <= multiplier <= 10
    ):
        errors.append("'retry.backoffMultiplier' must be a number between 1 and 10")


def validate_field(item: Any) -> list[str]:
    """Validates one field declaration."""
    if not isinstance(item, Mapping):
        return ["field must be an object"]
    errors = [f"unknown property '{k}'" for k in item if k not in _FIELD_KEYS]
    name, kind, item_type = item.get("name"), item.get("type"), item.get("itemType")
    if not isinstance(name, str) or not FIELD_NAME.match(name):
        errors.append("'name' must match ^[a-z_][a-z0-9_]{0,63}$")
    if kind not in ALL_TYPES:
        errors.append(f"'type' must be one of {sorted(ALL_TYPES)}")
        return errors

    if kind == "list":
        if item_type not in LIST_ITEM_TYPES:
            errors.append(f"list fields require 'itemType' in {sorted(LIST_ITEM_TYPES)}")
    elif item_type is not None:
        errors.append("'itemType' is only allowed on list fields")

    is_enum = kind == "enum" or (kind == "list" and item_type == "enum")
    enum_values = item.get("enumValues")
    if is_enum:
        if (
            not isinstance(enum_values, list)
            or not enum_values
            or len(enum_values) > 200
            or len(set(map(str, enum_values))) != len(enum_values)
        ):
            errors.append("enum fields require 1-200 unique 'enumValues'")
    elif enum_values is not None:
        errors.append("'enumValues' is only allowed on enum fields")

    is_file = kind == "file" or (kind == "list" and item_type == "file")
    if not is_file and item.get("contentTypes") is not None:
        errors.append("'contentTypes' is only allowed on file fields")
    if kind != "table" and item.get("columns") is not None:
        errors.append("'columns' is only allowed on table fields")
    columns = item.get("columns")
    if kind == "table" and columns is not None:
        if not isinstance(columns, list) or not columns:
            errors.append("'columns' must be a non-empty list")
        else:
            for column in columns:
                if (
                    not isinstance(column, Mapping)
                    or not isinstance(column.get("name"), str)
                    or column.get("type") not in SCALAR_TYPES
                ):
                    errors.append("each column needs a 'name' and a scalar 'type'")

    description = item.get("description")
    if description is not None and (not isinstance(description, str) or len(description) > 500):
        errors.append("'description' must be a string up to 500 chars")
    if "required" in item and not isinstance(item["required"], bool):
        errors.append("'required' must be a boolean")

    default = item.get("default")
    if default is not None:
        if is_file or kind == "table":
            errors.append(f"'{kind}' fields cannot have a default")
        elif not errors:
            try:
                wire_to_python(default, FieldSpec.from_dict(item), Path("."), f"{name}.default")
            except ContractError as exc:
                errors.append(f"invalid default: {exc.message}")
    return errors
