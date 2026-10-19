"""Spec 002 acceptance criteria exercised through the real entry point (``-m psrunner.host``)."""

from __future__ import annotations

import pytest
from conftest import RunHost

ALL_FIELDS = [
    {"name": "numero", "type": "number"},
    {"name": "inteiro", "type": "integer"},
    {"name": "valor", "type": "decimal"},
    {"name": "texto", "type": "string"},
    {"name": "ativo", "type": "bool"},
    {"name": "dia", "type": "date"},
    {"name": "momento", "type": "datetime"},
    {"name": "tipo", "type": "enum", "enumValues": ["aritmetica", "geometrica"]},
    {"name": "dados", "type": "json"},
    {"name": "tabela", "type": "table"},
    {"name": "lista", "type": "list", "itemType": "number"},
    {"name": "arquivo", "type": "file"},
    {"name": "anexos", "type": "list", "itemType": "file"},
]

ECHO_SCRIPT = """
    import shutil
    from psrunner import stage

    @stage
    def run(inputs, ctx):
        out = dict(inputs)
        source = inputs["arquivo"]
        copy = ctx.new_file("copia-" + source.name, source.content_type)
        shutil.copyfile(source.path, copy.path)
        out["arquivo"] = copy
        attachments = []
        for index, item in enumerate(inputs["anexos"]):
            target = ctx.new_file(f"{index}-{item.name}", item.content_type)
            shutil.copyfile(item.path, target.path)
            attachments.append(target)
        out["anexos"] = attachments
        out["tipos"] = {name: type(value).__name__ for name, value in inputs.items()}
        return out
"""


def test_spec002_ac01_all_types_round_trip(run_host: RunHost) -> None:
    inputs = {
        "numero": 1.5,
        "inteiro": 42,
        "valor": "1234.5600",
        "texto": "olá",
        "ativo": True,
        "dia": "2026-10-19",
        "momento": "2026-10-19T14:30:00-03:00",
        "tipo": "geometrica",
        "dados": {"a": [1, 2, {"b": None}]},
        "tabela": [{"i": 1, "x": 1.0}, {"i": 2, "x": 2.0}],
        "lista": [1, 2.5, 3],
    }
    files = {
        "arquivo": ("pagina.html", b"<h1>oi</h1>", "text/html"),
        "anexos": [("a.txt", b"A", "text/plain"), ("b.txt", b"B", "text/plain")],
    }
    output_fields = [*ALL_FIELDS, {"name": "tipos", "type": "json"}]

    run = run_host(ECHO_SCRIPT, inputs, ALL_FIELDS, output_fields, files=files)

    outputs = run.outputs
    assert outputs["tipos"] == {
        "numero": "float",
        "inteiro": "int",
        "valor": "Decimal",
        "texto": "str",
        "ativo": "bool",
        "dia": "date",
        "momento": "datetime",
        "tipo": "str",
        "dados": "dict",
        "tabela": "DataFrame",
        "lista": "list",
        "arquivo": "File",
        "anexos": "list",
    }
    for name in (
        "numero",
        "inteiro",
        "valor",
        "texto",
        "ativo",
        "dia",
        "momento",
        "tipo",
        "dados",
        "tabela",
    ):
        assert outputs[name] == inputs[name], name
    assert outputs["lista"] == [1.0, 2.5, 3.0]
    assert outputs["arquivo"]["$file"] == {
        "path": "out/copia-pagina.html",
        "name": "copia-pagina.html",
        "contentType": "text/html",
    }
    assert [a["$file"]["name"] for a in outputs["anexos"]] == ["0-a.txt", "1-b.txt"]
    assert (run.work_dir / "out" / "copia-pagina.html").read_bytes() == b"<h1>oi</h1>"


def test_spec002_ac02_decimal_keeps_every_digit(run_host: RunHost) -> None:
    script = """
        from decimal import Decimal
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            assert isinstance(inputs["valor"], Decimal)
            return {"valor": inputs["valor"]}
    """
    fields = [{"name": "valor", "type": "decimal"}]

    run = run_host(script, {"valor": "0.1000000000000000000000000001"}, fields, fields)

    assert run.outputs == {"valor": "0.1000000000000000000000000001"}


def test_spec002_ac04_stage_error_reports_code(run_host: RunHost) -> None:
    script = """
        from psrunner import StageError, stage

        @stage
        def run(inputs, ctx):
            raise StageError("html sem body", code="E1", details={"linha": 3})
    """

    run = run_host(script)

    assert run.returncode == 0
    assert run.error["kind"] == "ScriptError"
    assert run.error["code"] == "E1"
    assert run.error["message"] == "html sem body"
    assert run.error["details"] == {"linha": 3}


def test_spec002_ac05_unhandled_exception_has_user_traceback(run_host: RunHost) -> None:
    script = """
        from psrunner import stage

        def helper():
            return 1 / 0

        @stage
        def run(inputs, ctx):
            return {"x": helper()}
    """

    run = run_host(script)

    assert run.error["kind"] == "ScriptError"
    assert run.error["message"].startswith("ZeroDivisionError")
    assert "script.py" in run.error["traceback"]
    assert "line 5" in run.error["traceback"]
    assert "psrunner" not in run.error["traceback"].split("script.py")[0].replace(
        "psrunner_stage", ""
    )


@pytest.mark.parametrize(
    ("script", "code"),
    [
        ("def run(inputs, ctx):\n    return {}\n", "NO_STAGE_FUNCTION"),
        (
            "from psrunner import stage\n"
            "@stage\ndef a(i, c):\n    return {}\n"
            "@stage\ndef b(i, c):\n    return {}\n",
            "MULTIPLE_STAGE_FUNCTIONS",
        ),
        ("def run(:\n", "SYNTAX_ERROR"),
    ],
)
def test_spec002_ac06_script_must_have_exactly_one_stage(
    run_host: RunHost, script: str, code: str
) -> None:
    run = run_host(script)

    assert run.error["kind"] == "ScriptError"
    assert run.error["code"] == code


def test_spec002_ac07_new_file_is_created_in_out(run_host: RunHost) -> None:
    script = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            pdf = ctx.new_file("a.pdf")
            pdf.path.write_bytes(b"%PDF-1.7")
            return {"pdf": pdf}
    """

    run = run_host(script, output_fields=[{"name": "pdf", "type": "file"}])

    assert run.outputs["pdf"] == {
        "$file": {"path": "out/a.pdf", "name": "a.pdf", "contentType": "application/pdf"}
    }


def test_spec002_ac08_file_outside_out_is_rejected(run_host: RunHost) -> None:
    script = """
        from psrunner import File, stage

        @stage
        def run(inputs, ctx):
            target = ctx.tmp_dir / "segredo.txt"
            target.write_text("x")
            return {"arquivo": File(target)}
    """

    run = run_host(script, output_fields=[{"name": "arquivo", "type": "file"}])

    assert run.error["kind"] == "OutputContractViolation"
    assert run.error["code"] == "OUTPUT_FILE_OUTSIDE_OUT"


def test_spec002_ac09_only_declared_secrets_are_readable(run_host: RunHost) -> None:
    script = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            try:
                ctx.secrets["OUTRO"]
                missing = ""
            except KeyError as exc:
                missing = str(exc)
            try:
                ctx.secrets["SMTP_SENHA"] = "x"
                writable = True
            except TypeError:
                writable = False
            return {"valor": ctx.secrets["SMTP_SENHA"], "erro": missing, "gravavel": writable}
    """
    outputs = [
        {"name": "valor", "type": "string"},
        {"name": "erro", "type": "string"},
        {"name": "gravavel", "type": "bool"},
    ]

    run = run_host(script, output_fields=outputs, secrets={"SMTP_SENHA": "abc"})

    assert run.outputs["valor"] == "abc"
    assert "SMTP_SENHA" in run.outputs["erro"]
    assert run.outputs["gravavel"] is False


def test_spec002_ac10_memory_limit(run_host: RunHost) -> None:
    script = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            data = bytearray(1024 * 1024 * 1024)
            return {"tamanho": len(data)}
    """

    run = run_host(
        script, output_fields=[{"name": "tamanho", "type": "integer"}], memory_limit_mb=128
    )

    assert run.error["kind"] == "MemoryLimitExceeded"


def test_spec002_ac11_protocol_mismatch(run_host: RunHost) -> None:
    run = run_host("from psrunner import stage\n", protocol="2")

    assert run.error["code"] == "PROTOCOL_MISMATCH"


def test_spec002_ac12_async_run_is_awaited(run_host: RunHost) -> None:
    script = """
        import asyncio
        from psrunner import stage

        @stage
        async def run(inputs, ctx):
            await asyncio.sleep(0.01)
            return {"ok": True}
    """

    run = run_host(script, output_fields=[{"name": "ok", "type": "bool"}])

    assert run.outputs == {"ok": True}


@pytest.mark.parametrize(
    ("body", "code"),
    [
        ('return {"x": 1, "extra": 2}', "UNDECLARED_OUTPUT"),
        ("return {}", "MISSING_REQUIRED_OUTPUT"),
        ('return {"x": float("nan")}', "NON_FINITE_NUMBER"),
        ('return {"x": "1"}', "TYPE_MISMATCH"),
        ("return [1]", "OUTPUT_NOT_A_DICT"),
    ],
)
def test_output_contract_violations(run_host: RunHost, body: str, code: str) -> None:
    script = f"""
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            {body}
    """

    run = run_host(script, output_fields=[{"name": "x", "type": "number"}])

    assert run.error["kind"] == "OutputContractViolation"
    assert run.error["code"] == code


def test_naive_datetime_output_is_rejected(run_host: RunHost) -> None:
    script = """
        from datetime import datetime
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            return {"quando": datetime(2026, 10, 19, 12, 0)}
    """

    run = run_host(script, output_fields=[{"name": "quando", "type": "datetime"}])

    assert run.error["code"] == "NAIVE_DATETIME"


def test_sys_exit_is_a_script_error(run_host: RunHost) -> None:
    script = """
        import sys
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            sys.exit(3)
    """

    run = run_host(script)

    assert run.error["code"] == "SCRIPT_EXITED"


def test_print_and_logging_do_not_break_the_protocol(run_host: RunHost) -> None:
    script = """
        from psrunner import stage

        print("hello from stdout")

        @stage
        def run(inputs, ctx):
            print("running")
            ctx.log.info("calculado %d termos", 4)
            return {"ok": True}
    """

    run = run_host(script, output_fields=[{"name": "ok", "type": "bool"}])

    assert run.outputs == {"ok": True}
    assert "hello from stdout" in run.stdout
    assert "INFO calculado 4 termos" in run.stderr


def test_missing_required_input_is_input_validation(run_host: RunHost) -> None:
    script = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            return {}
    """

    run = run_host(script, {}, [{"name": "n", "type": "integer"}])

    assert run.error["kind"] == "InputValidation"
    assert run.error["code"] == "MISSING_REQUIRED_INPUT"
