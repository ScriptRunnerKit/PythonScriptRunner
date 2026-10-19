"""Conversion between the wire format (JSON) and Python values, with contract validation."""

from __future__ import annotations

import importlib.util
import json
import math
import os
import re
from collections.abc import Mapping, Sequence
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path, PurePath
from typing import Any

from psrunner.errors import ContractError
from psrunner.fields import FieldSpec
from psrunner.files import File, guess_content_type
from psrunner.protocol import FILE_MARKER, KIND_INPUT_VALIDATION, KIND_OUTPUT_CONTRACT

_DATETIME = re.compile(
    r"^(?P<date>\d{4}-\d{2}-\d{2})[T ](?P<h>\d{2}):(?P<m>\d{2})"
    r"(?::(?P<s>\d{2})(?:[.,](?P<f>\d+))?)?"
    r"(?P<tz>Z|z|[+-]\d{2}:?\d{2})?$"
)

_pandas_loaded = False
_pandas: Any = None


def get_pandas() -> Any:
    """Returns the pandas module when installed (imported lazily, it is slow to import)."""
    global _pandas_loaded, _pandas
    if not _pandas_loaded:
        _pandas_loaded = True
        if importlib.util.find_spec("pandas") is not None:
            import pandas

            _pandas = pandas
    return _pandas


def _error(kind: str, code: str, path: str, message: str) -> ContractError:
    return ContractError(kind, code, f"'{path}': {message}")


def _type_name(value: Any) -> str:
    return type(value).__name__


# ---------------------------------------------------------------------------
# Wire -> Python (inputs received from the runner)
# ---------------------------------------------------------------------------


def inputs_to_python(
    raw: Mapping[str, Any], specs: Sequence[FieldSpec], work_dir: Path
) -> dict[str, Any]:
    """Converts wire inputs to Python values. Absent optional inputs become ``None``."""
    declared = {spec.name for spec in specs}
    undeclared = sorted(name for name in raw if name not in declared)
    if undeclared:
        raise ContractError(
            KIND_INPUT_VALIDATION,
            "UNDECLARED_INPUT",
            f"Inputs not declared by the stage: {', '.join(undeclared)}.",
        )

    result: dict[str, Any] = {}
    for spec in specs:
        value = raw.get(spec.name)
        if value is None and spec.has_default:
            value = spec.default
        if value is None:
            if spec.required:
                raise _error(
                    KIND_INPUT_VALIDATION,
                    "MISSING_REQUIRED_INPUT",
                    spec.name,
                    "required input is missing",
                )
            result[spec.name] = None
            continue
        result[spec.name] = wire_to_python(value, spec, work_dir, spec.name)
    return result


def wire_to_python(value: Any, spec: FieldSpec, work_dir: Path, path: str) -> Any:
    kind = KIND_INPUT_VALIDATION
    t = spec.type
    if t == "number":
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected number, got {_type_name(value)}")
        number = float(value)
        if not math.isfinite(number):
            raise _error(kind, "NON_FINITE_NUMBER", path, "number must be finite")
        return number
    if t == "integer":
        if isinstance(value, bool) or not isinstance(value, int):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected integer, got {_type_name(value)}")
        return value
    if t == "decimal":
        return _parse_decimal(value, kind, path)
    if t == "string":
        if not isinstance(value, str):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected string, got {_type_name(value)}")
        return value
    if t == "bool":
        if not isinstance(value, bool):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected bool, got {_type_name(value)}")
        return value
    if t == "date":
        return _parse_date(value, kind, path)
    if t == "datetime":
        return _parse_datetime(value, kind, path)
    if t == "enum":
        return _check_enum(value, spec, kind, path)
    if t == "json":
        return value
    if t == "table":
        return _table_to_python(value, spec, kind, path)
    if t == "file":
        return _file_from_wire(value, work_dir, kind, path)
    if t == "list":
        if not isinstance(value, list):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected list, got {_type_name(value)}")
        item = spec.item_spec()
        return [wire_to_python(v, item, work_dir, f"{path}[{i}]") for i, v in enumerate(value)]
    raise _error(kind, "UNKNOWN_TYPE", path, f"unknown field type '{t}'")


def _parse_decimal(value: Any, kind: str, path: str) -> Decimal:
    if isinstance(value, bool) or not isinstance(value, (str, int, Decimal)):
        raise _error(
            kind, "TYPE_MISMATCH", path, f"expected decimal string, got {_type_name(value)}"
        )
    try:
        number = Decimal(str(value).strip())
    except InvalidOperation:
        raise _error(kind, "INVALID_DECIMAL", path, f"'{value}' is not a valid decimal") from None
    if not number.is_finite():
        raise _error(kind, "NON_FINITE_NUMBER", path, "decimal must be finite")
    return number


def _parse_date(value: Any, kind: str, path: str) -> date:
    if not isinstance(value, str):
        raise _error(
            kind, "TYPE_MISMATCH", path, f"expected ISO date string, got {_type_name(value)}"
        )
    try:
        return date.fromisoformat(value)
    except ValueError:
        raise _error(kind, "INVALID_DATE", path, f"'{value}' is not an ISO 8601 date") from None


def parse_datetime(text: str) -> datetime | None:
    """Parses ISO 8601 date-times with any fraction length on every supported Python version."""
    match = _DATETIME.match(text)
    if match is None:
        return None
    year, month, day = (int(p) for p in match["date"].split("-"))
    fraction = (match["f"] or "0")[:6].ljust(6, "0")
    tz_text = match["tz"]
    tzinfo: timezone | None = None
    if tz_text:
        if tz_text in {"Z", "z"}:
            tzinfo = timezone.utc
        else:
            sign = -1 if tz_text[0] == "-" else 1
            digits = tz_text[1:].replace(":", "")
            offset = timedelta(hours=int(digits[:2]), minutes=int(digits[2:]))
            tzinfo = timezone(sign * offset)
    try:
        return datetime(
            year,
            month,
            day,
            int(match["h"]),
            int(match["m"]),
            int(match["s"] or 0),
            int(fraction),
            tzinfo=tzinfo,
        )
    except ValueError:
        return None


def _parse_datetime(value: Any, kind: str, path: str) -> datetime:
    if not isinstance(value, str):
        raise _error(
            kind, "TYPE_MISMATCH", path, f"expected ISO date-time string, got {_type_name(value)}"
        )
    parsed = parse_datetime(value)
    if parsed is None:
        raise _error(kind, "INVALID_DATETIME", path, f"'{value}' is not an ISO 8601 date-time")
    if parsed.tzinfo is None:
        raise _error(kind, "NAIVE_DATETIME", path, "date-time must include a UTC offset")
    return parsed


def _check_enum(value: Any, spec: FieldSpec, kind: str, path: str) -> str:
    if not isinstance(value, str):
        raise _error(kind, "TYPE_MISMATCH", path, f"expected enum string, got {_type_name(value)}")
    allowed = spec.enum_values or ()
    if value not in allowed:
        raise _error(
            kind, "ENUM_VALUE_INVALID", path, f"'{value}' is not one of: {', '.join(allowed)}"
        )
    return value


def _table_to_python(value: Any, spec: FieldSpec, kind: str, path: str) -> Any:
    if not isinstance(value, list) or not all(isinstance(row, Mapping) for row in value):
        raise _error(kind, "TYPE_MISMATCH", path, "expected a list of objects (records)")
    rows: list[dict[str, Any]] = [dict(row) for row in value]
    if spec.columns:
        for index, row in enumerate(rows):
            for column in spec.columns:
                cell_path = f"{path}[{index}].{column.name}"
                if column.name not in row:
                    raise _error(kind, "MISSING_COLUMN", cell_path, "declared column is missing")
                cell = row[column.name]
                if cell is not None:
                    row[column.name] = wire_to_python(
                        cell, FieldSpec(name=column.name, type=column.type), Path("."), cell_path
                    )
    pandas = get_pandas()
    if pandas is None:
        return rows
    columns = [c.name for c in spec.columns] if spec.columns else None
    if columns is not None:
        extra = [k for row in rows for k in row if k not in columns]
        columns += list(dict.fromkeys(extra))
    return pandas.DataFrame.from_records(rows, columns=columns)


def _file_from_wire(value: Any, work_dir: Path, kind: str, path: str) -> File:
    info = value.get(FILE_MARKER) if isinstance(value, Mapping) else None
    if not isinstance(info, Mapping) or not isinstance(info.get("path"), str):
        raise _error(kind, "TYPE_MISMATCH", path, "expected a file reference")
    return File(
        work_dir / info["path"],
        info.get("name"),
        info.get("contentType"),
        size=info.get("size"),
        sha256=info.get("sha256"),
    )


# ---------------------------------------------------------------------------
# Python -> wire (outputs produced by the script)
# ---------------------------------------------------------------------------


def outputs_to_wire(result: Any, specs: Sequence[FieldSpec], work_dir: Path) -> dict[str, Any]:
    """Validates the dict returned by ``run()`` against the declared outputs."""
    kind = KIND_OUTPUT_CONTRACT
    if not isinstance(result, Mapping):
        raise ContractError(
            kind, "OUTPUT_NOT_A_DICT", f"run() must return a dict, got {_type_name(result)}."
        )
    declared = {spec.name for spec in specs}
    undeclared = sorted(str(name) for name in result if name not in declared)
    if undeclared:
        raise ContractError(
            kind,
            "UNDECLARED_OUTPUT",
            f"Outputs not declared by the stage: {', '.join(undeclared)}.",
        )

    wire: dict[str, Any] = {}
    for spec in specs:
        value = result.get(spec.name)
        if value is None:
            if spec.required:
                raise _error(
                    kind, "MISSING_REQUIRED_OUTPUT", spec.name, "required output is missing"
                )
            continue
        wire[spec.name] = python_to_wire(value, spec, work_dir, spec.name, kind)
    return wire


def _unwrap_numpy(value: Any) -> Any:
    if type(value).__module__ == "numpy" and hasattr(value, "item"):
        return value.item()
    return value


def python_to_wire(value: Any, spec: FieldSpec, work_dir: Path, path: str, kind: str) -> Any:
    value = _unwrap_numpy(value)
    t = spec.type
    if t == "number":
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected number, got {_type_name(value)}")
        if isinstance(value, float) and not math.isfinite(value):
            raise _error(kind, "NON_FINITE_NUMBER", path, "number must be finite (no NaN/Infinity)")
        return value
    if t == "integer":
        if isinstance(value, bool) or not isinstance(value, int):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected integer, got {_type_name(value)}")
        return value
    if t == "decimal":
        if isinstance(value, float):
            raise _error(
                kind, "TYPE_MISMATCH", path, "expected Decimal, int or str; float loses precision"
            )
        return format(_parse_decimal(value, kind, path), "f")
    if t == "string":
        if not isinstance(value, str):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected string, got {_type_name(value)}")
        return value
    if t == "bool":
        if not isinstance(value, bool):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected bool, got {_type_name(value)}")
        return value
    if t == "date":
        if isinstance(value, datetime) or not isinstance(value, date):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected date, got {_type_name(value)}")
        return value.isoformat()
    if t == "datetime":
        if not isinstance(value, datetime):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected datetime, got {_type_name(value)}")
        if value.tzinfo is None or value.utcoffset() is None:
            raise _error(kind, "NAIVE_DATETIME", path, "datetime must be timezone-aware")
        return value.isoformat()
    if t == "enum":
        return _check_enum(value, spec, kind, path)
    if t == "json":
        _ensure_json(value, kind, path)
        return value
    if t == "table":
        return _table_to_wire(value, spec, work_dir, kind, path)
    if t == "file":
        return _file_to_wire(value, work_dir, kind, path)
    if t == "list":
        if isinstance(value, (str, bytes, Mapping)) or not isinstance(value, (list, tuple)):
            raise _error(kind, "TYPE_MISMATCH", path, f"expected list, got {_type_name(value)}")
        item = spec.item_spec()
        return [
            python_to_wire(v, item, work_dir, f"{path}[{i}]", kind) for i, v in enumerate(value)
        ]
    raise _error(kind, "UNKNOWN_TYPE", path, f"unknown field type '{t}'")


def _ensure_json(value: Any, kind: str, path: str) -> None:
    try:
        json.dumps(value, allow_nan=False)
    except (TypeError, ValueError) as exc:
        raise _error(kind, "NOT_JSON_SERIALIZABLE", path, str(exc)) from None


def _table_to_wire(value: Any, spec: FieldSpec, work_dir: Path, kind: str, path: str) -> Any:
    pandas = get_pandas()
    if pandas is not None and isinstance(value, pandas.DataFrame):
        rows: list[Any] = value.to_dict(orient="records")
    elif isinstance(value, (list, tuple)):
        rows = list(value)
    else:
        raise _error(
            kind,
            "TYPE_MISMATCH",
            path,
            f"expected DataFrame or list of dicts, got {_type_name(value)}",
        )

    column_types = {c.name: c.type for c in spec.columns} if spec.columns else {}
    wire_rows: list[dict[str, Any]] = []
    for index, row in enumerate(rows):
        row_path = f"{path}[{index}]"
        if not isinstance(row, Mapping):
            raise _error(kind, "TYPE_MISMATCH", row_path, "each row must be a dict")
        missing = [name for name in column_types if name not in row]
        if missing:
            raise _error(kind, "MISSING_COLUMN", row_path, f"missing columns: {', '.join(missing)}")
        wire_rows.append(
            {
                str(name): _cell_to_wire(
                    cell, column_types.get(str(name)), work_dir, kind, f"{row_path}.{name}"
                )
                for name, cell in row.items()
            }
        )
    return wire_rows


def _is_missing(value: Any) -> bool:
    if value is None:
        return True
    if isinstance(value, float) and math.isnan(value):
        return True
    pandas = get_pandas()
    return pandas is not None and value is pandas.NaT


def _cell_to_wire(value: Any, column_type: str | None, work_dir: Path, kind: str, path: str) -> Any:
    value = _unwrap_numpy(value)
    if _is_missing(value):
        return None
    if hasattr(value, "to_pydatetime"):
        value = value.to_pydatetime()
    if column_type is not None:
        if column_type == "date" and isinstance(value, datetime):
            value = value.date()
        return python_to_wire(value, FieldSpec(name=path, type=column_type), work_dir, path, kind)
    if isinstance(value, Decimal):
        return format(value, "f")
    if isinstance(value, (date, datetime)):
        return value.isoformat()
    _ensure_json(value, kind, path)
    return value


def _file_to_wire(value: Any, work_dir: Path, kind: str, path: str) -> dict[str, Any]:
    if isinstance(value, File):
        file_path, name, content_type = value.path, value.name, value.content_type
    elif isinstance(value, (PurePath, os.PathLike)):
        file_path = Path(value)
        name = file_path.name
        content_type = guess_content_type(name)
    else:
        raise _error(kind, "TYPE_MISMATCH", path, f"expected File or Path, got {_type_name(value)}")

    full = Path(file_path)
    if not full.is_absolute():
        full = work_dir / full
    out_dir = (work_dir / "out").resolve()
    if full.is_symlink():
        raise _error(kind, "OUTPUT_FILE_OUTSIDE_OUT", path, "symbolic links are not allowed")
    try:
        resolved = full.resolve(strict=True)
    except (FileNotFoundError, OSError):
        raise _error(
            kind, "OUTPUT_FILE_MISSING", path, f"file was not written: {full.name}"
        ) from None
    if not resolved.is_relative_to(out_dir):
        raise _error(
            kind,
            "OUTPUT_FILE_OUTSIDE_OUT",
            path,
            "output files must be created inside out/ (use ctx.new_file)",
        )
    if not resolved.is_file():
        raise _error(kind, "OUTPUT_FILE_MISSING", path, f"not a regular file: {full.name}")
    relative = resolved.relative_to(work_dir.resolve()).as_posix()
    return {FILE_MARKER: {"path": relative, "name": name, "contentType": content_type}}
