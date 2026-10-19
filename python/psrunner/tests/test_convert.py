from __future__ import annotations

from datetime import date, datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path

import pandas as pd
import pytest

from psrunner import convert
from psrunner.errors import ContractError
from psrunner.fields import Column, FieldSpec

TABLE = FieldSpec(name="tabela", type="table")


def test_spec002_ac03_table_without_pandas_is_list_of_dicts(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(convert, "_pandas_loaded", True)
    monkeypatch.setattr(convert, "_pandas", None)

    value = convert.wire_to_python([{"i": 1}], TABLE, Path("."), "tabela")

    assert value == [{"i": 1}]


def test_spec002_ac03_table_with_pandas_is_dataframe() -> None:
    value = convert.wire_to_python([{"i": 1, "x": 2.0}], TABLE, Path("."), "tabela")

    assert isinstance(value, pd.DataFrame)
    assert list(value.columns) == ["i", "x"]


def test_spec002_ac03_dataframe_output_dates_become_iso() -> None:
    frame = pd.DataFrame(
        {
            "dia": pd.to_datetime(["2026-10-19", "2026-10-20"]),
            "valor": [1.5, float("nan")],
        }
    )

    wire = convert.python_to_wire(frame, TABLE, Path("."), "tabela", "OutputContractViolation")

    assert wire == [
        {"dia": "2026-10-19T00:00:00", "valor": 1.5},
        {"dia": "2026-10-20T00:00:00", "valor": None},
    ]


def test_table_columns_are_converted_and_required() -> None:
    spec = FieldSpec(
        name="t", type="table", columns=(Column("dia", "date"), Column("n", "integer"))
    )

    rows = convert.wire_to_python([{"dia": "2026-10-19", "n": 1}], spec, Path("."), "t")
    assert rows["dia"][0] == date(2026, 10, 19)

    with pytest.raises(ContractError) as error:
        convert.python_to_wire(
            [{"dia": date(2026, 10, 19)}], spec, Path("."), "t", "OutputContractViolation"
        )
    assert error.value.code == "MISSING_COLUMN"


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        (
            "2026-10-19T14:30:00-03:00",
            datetime(2026, 10, 19, 14, 30, tzinfo=timezone(timedelta(hours=-3))),
        ),
        ("2026-10-19T17:30:00Z", datetime(2026, 10, 19, 17, 30, tzinfo=timezone.utc)),
        (
            "2026-10-19T17:30:00.1234567+00:00",
            datetime(2026, 10, 19, 17, 30, 0, 123456, tzinfo=timezone.utc),
        ),
        (
            "2026-10-19T17:30:00.5+0000",
            datetime(2026, 10, 19, 17, 30, 0, 500000, tzinfo=timezone.utc),
        ),
    ],
)
def test_datetime_parsing_accepts_dotnet_formats(text: str, expected: datetime) -> None:
    assert convert.parse_datetime(text) == expected


def test_naive_datetime_input_is_rejected() -> None:
    spec = FieldSpec(name="m", type="datetime")

    with pytest.raises(ContractError) as error:
        convert.wire_to_python("2026-10-19T10:00:00", spec, Path("."), "m")

    assert error.value.code == "NAIVE_DATETIME"


def test_decimal_output_never_uses_exponent() -> None:
    spec = FieldSpec(name="v", type="decimal")

    assert convert.python_to_wire(Decimal("1E-7"), spec, Path("."), "v", "x") == "0.0000001"
    assert convert.python_to_wire(10, spec, Path("."), "v", "x") == "10"


def test_decimal_output_rejects_float() -> None:
    spec = FieldSpec(name="v", type="decimal")

    with pytest.raises(ContractError):
        convert.python_to_wire(0.1, spec, Path("."), "v", "x")


def test_enum_list_validates_each_item() -> None:
    spec = FieldSpec(name="tipos", type="list", item_type="enum", enum_values=("a", "b"))

    assert convert.wire_to_python(["a", "b"], spec, Path("."), "tipos") == ["a", "b"]
    with pytest.raises(ContractError) as error:
        convert.wire_to_python(["a", "c"], spec, Path("."), "tipos")
    assert "tipos[1]" in error.value.message


def test_integer_rejects_bool_and_float() -> None:
    spec = FieldSpec(name="n", type="integer")

    for value in (True, 1.0):
        with pytest.raises(ContractError):
            convert.wire_to_python(value, spec, Path("."), "n")


def test_default_applies_to_absent_optional_input() -> None:
    spec = FieldSpec(
        name="tipo", type="enum", required=False, has_default=True, default="a", enum_values=("a",)
    )

    assert convert.inputs_to_python({}, [spec], Path(".")) == {"tipo": "a"}


def test_absent_optional_input_without_default_is_none() -> None:
    spec = FieldSpec(name="anexo", type="file", required=False)

    assert convert.inputs_to_python({}, [spec], Path(".")) == {"anexo": None}
