"""Field declarations (mirror of the .NET ``FieldDefinition``)."""

from __future__ import annotations

import re
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Any

SCALAR_TYPES = frozenset({"number", "integer", "decimal", "string", "bool", "date", "datetime"})
ALL_TYPES = SCALAR_TYPES | {"enum", "json", "table", "file", "list"}
LIST_ITEM_TYPES = SCALAR_TYPES | {"enum", "file"}
FIELD_NAME = re.compile(r"^[a-z_][a-z0-9_]{0,63}$")


@dataclass(frozen=True)
class Column:
    """A declared column of a ``table`` field."""

    name: str
    type: str


@dataclass(frozen=True)
class FieldSpec:
    """One declared input or output of a stage."""

    name: str
    type: str
    item_type: str | None = None
    required: bool = True
    has_default: bool = False
    default: Any = None
    enum_values: tuple[str, ...] | None = None
    content_types: tuple[str, ...] | None = None
    columns: tuple[Column, ...] | None = None

    @property
    def is_file(self) -> bool:
        return self.type == "file" or (self.type == "list" and self.item_type == "file")

    def item_spec(self) -> FieldSpec:
        """Spec used to convert each item of a ``list`` field."""
        return FieldSpec(
            name=self.name,
            type=self.item_type or "json",
            enum_values=self.enum_values,
            content_types=self.content_types,
        )

    @staticmethod
    def from_dict(data: Mapping[str, Any]) -> FieldSpec:
        """Builds a spec from the wire/manifest representation (camelCase keys)."""
        columns_raw = data.get("columns")
        columns = (
            tuple(Column(str(c["name"]), str(c["type"])) for c in columns_raw)
            if columns_raw
            else None
        )
        enum_values = data.get("enumValues")
        content_types = data.get("contentTypes")
        default = data.get("default")
        return FieldSpec(
            name=str(data["name"]),
            type=str(data["type"]),
            item_type=data.get("itemType"),
            required=bool(data.get("required", True)),
            has_default=default is not None,
            default=default,
            enum_values=tuple(enum_values) if enum_values else None,
            content_types=tuple(content_types) if content_types else None,
            columns=columns,
        )


def specs_from_dicts(items: Any) -> list[FieldSpec]:
    """Converts a list of field dictionaries into specs (``None`` means no fields)."""
    if not items:
        return []
    return [FieldSpec.from_dict(item) for item in items]
