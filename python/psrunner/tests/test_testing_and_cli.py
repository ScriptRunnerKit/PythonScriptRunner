from __future__ import annotations

import json
from pathlib import Path

import pytest

from psrunner.cli import main as cli_main
from psrunner.manifest import ManifestError, load_manifest, validate_manifest_data
from psrunner.testing import run_stage

SCRIPT = """
from psrunner import stage

@stage
def run(inputs, ctx):
    a1, razao, n = inputs["a1"], inputs["razao"], inputs["n"]
    if inputs["tipo"] == "geometrica":
        termos = [a1 * razao**i for i in range(n)]
    else:
        termos = [a1 + razao * i for i in range(n)]
    ctx.log.info("gerados %d termos", n)
    return {"termos": termos}
"""

MANIFEST = {
    "key": "progressao-termos",
    "version": 1,
    "name": "Termos de progressão",
    "inputs": [
        {"name": "a1", "type": "number"},
        {"name": "razao", "type": "number"},
        {"name": "n", "type": "integer"},
        {
            "name": "tipo",
            "type": "enum",
            "enumValues": ["aritmetica", "geometrica"],
            "required": False,
            "default": "aritmetica",
        },
    ],
    "outputs": [{"name": "termos", "type": "list", "itemType": "number"}],
}


@pytest.fixture
def stage_dir(tmp_path: Path) -> Path:
    folder = tmp_path / "progressao-termos"
    folder.mkdir()
    (folder / "stage.json").write_text(json.dumps(MANIFEST), encoding="utf-8")
    (folder / "script.py").write_text(SCRIPT, encoding="utf-8")
    return folder


def test_spec002_ac13_run_stage_validates_against_stage_json(stage_dir: Path) -> None:
    result = run_stage(
        stage_dir / "script.py",
        {"a1": 1, "razao": 2, "n": 4, "tipo": "geometrica"},
        stage_json=stage_dir / "stage.json",
    )

    assert result.ok, result.error
    assert result.outputs == {"termos": [1.0, 2.0, 4.0, 8.0]}
    assert result.wire_outputs == {"termos": [1.0, 2.0, 4.0, 8.0]}
    assert result.logs == ["INFO gerados 4 termos"]


def test_run_stage_applies_defaults(stage_dir: Path) -> None:
    result = run_stage(
        stage_dir / "script.py", {"a1": 1, "razao": 2, "n": 3}, stage_json=stage_dir / "stage.json"
    )

    assert result.outputs == {"termos": [1.0, 3.0, 5.0]}


def test_run_stage_rejects_invalid_input(stage_dir: Path) -> None:
    result = run_stage(
        stage_dir / "script.py",
        {"a1": 1, "razao": 2, "n": "4"},
        stage_json=stage_dir / "stage.json",
    )

    assert not result.ok
    assert result.error is not None
    assert result.error.kind == "InputValidation"


def test_run_stage_accepts_callables_and_files(tmp_path: Path) -> None:
    source = tmp_path / "pagina.html"
    source.write_text("<h1>oi</h1>", encoding="utf-8")

    def run(inputs: dict[str, object], ctx: object) -> dict[str, object]:
        html = inputs["html"]
        return {"tamanho": len(html.read_text())}  # type: ignore[attr-defined]

    result = run_stage(run, files={"html": source})

    assert result.outputs == {"tamanho": 11}


def test_manifest_loads(stage_dir: Path) -> None:
    manifest = load_manifest(stage_dir)

    assert manifest.key == "progressao-termos"
    assert [f.name for f in manifest.inputs] == ["a1", "razao", "n", "tipo"]


@pytest.mark.parametrize(
    ("change", "fragment"),
    [
        ({"key": "X"}, "'key'"),
        ({"inputs": [{"name": "a", "type": "number", "itemType": "number"}]}, "itemType"),
        ({"inputs": [{"name": "a", "type": "enum"}]}, "enumValues"),
        ({"inputs": [{"name": "f", "type": "file", "default": "x"}]}, "cannot have a default"),
        (
            {"inputs": [{"name": "a", "type": "number"}, {"name": "a", "type": "number"}]},
            "duplicate",
        ),
        ({"inputs": [{"name": "n", "type": "integer", "default": "1"}]}, "invalid default"),
        ({"python": "2.7"}, "'python'"),
        ({"secrets": ["smtp"]}, "secret name"),
        ({"unknown": 1}, "unknown property"),
    ],
)
def test_manifest_validation_errors(change: dict[str, object], fragment: str) -> None:
    errors = validate_manifest_data({**MANIFEST, **change})

    assert any(fragment in e for e in errors), errors


def test_invalid_manifest_raises(tmp_path: Path) -> None:
    (tmp_path / "stage.json").write_text("{}", encoding="utf-8")

    with pytest.raises(ManifestError):
        load_manifest(tmp_path)


def test_cli_validate(stage_dir: Path, capsys: pytest.CaptureFixture[str]) -> None:
    assert cli_main(["validate", str(stage_dir)]) == 0
    assert "OK: progressao-termos@1" in capsys.readouterr().out


def test_cli_validate_reports_missing_stage(
    stage_dir: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    (stage_dir / "script.py").write_text("def run(i, c):\n    return {}\n", encoding="utf-8")

    assert cli_main(["validate", str(stage_dir)]) == 1
    assert "NO_STAGE_FUNCTION" in capsys.readouterr().err


def test_cli_run_uses_the_real_protocol(
    stage_dir: Path, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    sample = tmp_path / "sample.json"
    sample.write_text(
        json.dumps({"a1": 1, "razao": 2, "n": 4, "tipo": "geometrica"}), encoding="utf-8"
    )

    code = cli_main(["run", str(stage_dir), "--input", str(sample)])

    out = capsys.readouterr().out
    assert code == 0, out
    assert '"termos": [' in out
