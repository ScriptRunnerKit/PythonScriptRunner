using PythonScriptRunner.Core.Pipelines;

namespace PythonScriptRunner.Core.Tests.Unit;

public sealed class PipelineRulesTests
{
    private static readonly StageDefinition Producer = Stage("produtor",
        inputs: [new("n", FieldType.Integer)],
        outputs: [new("valor", FieldType.Integer), new("talvez", FieldType.Number) { Required = false }, new("rotulo", FieldType.Enum("a", "b"))]);

    private static readonly StageDefinition Consumer = Stage("consumidor",
        inputs: [new("valor", FieldType.Number), new("porta", FieldType.Integer) { Required = false, Default = 465 }]);

    [Fact]
    public void ValidPipeline_HasNoErrors()
    {
        var report = Validate(Pipeline(
            [new("n", FieldType.Integer)],
            [Step("p", "produtor"), Step("c", "consumidor")],
            [new PipelineOutputDefinition("valor", From.Step("p", "valor"))]));

        report.IsValid.ShouldBeTrue(report.ToString());
        report.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(InvalidPipelines))]
    public void Spec003_AC05_EachRuleReportsItsCodeStepAndField(string code, PipelineDefinition pipeline, string? step, string? field)
    {
        var report = Validate(pipeline);

        var issue = report.Errors.ShouldHaveSingleItem(report.ToString());
        issue.Code.ShouldBe(code);
        issue.Step.ShouldBe(step);
        issue.Field.ShouldBe(field);
    }

    public static TheoryData<string, PipelineDefinition, string?, string?> InvalidPipelines() => new()
    {
        { "PIPE_DEFINITION", Pipeline([new("N", FieldType.Integer)], [Step("p", "produtor", ("n", From.Constant(1)))], []), null, null },
        { "PIPE_NO_STEPS", Pipeline([], [], []), null, null },
        { "PIPE_STEP_KEY", Pipeline([new("n", FieldType.Integer)], [Step("p", "produtor"), Step("p", "produtor", ("n", From.Pipeline("n")))], []), "p", null },
        { "PIPE_STAGE_NOT_PUBLISHED", Pipeline([new("n", FieldType.Integer)], [Step("p", "produtor"), new PipelineStepDefinition("x", "produtor", 9)], []), "x", null },
        { "PIPE_FORWARD_REF", Pipeline([new("n", FieldType.Integer)], [Step("c", "consumidor", ("valor", From.Step("p", "valor"))), Step("p", "produtor", ("n", From.Pipeline("n")))], []), "c", "valor" },
        { "PIPE_UNKNOWN_SOURCE", Pipeline([new("n", FieldType.Integer)], [Step("p", "produtor"), Step("c", "consumidor", ("valor", From.Step("p", "inexistente")))], []), "c", "valor" },
        { "PIPE_UNKNOWN_INPUT", Pipeline([new("n", FieldType.Integer)], [Step("p", "produtor", ("nao_existe", From.Constant(1)))], []), "p", "nao_existe" },
        { "PIPE_UNBOUND_REQUIRED", Pipeline([], [Step("p", "produtor")], []), "p", "n" },
        { "PIPE_TYPE_MISMATCH", Pipeline([new("n", FieldType.Number)], [Step("p", "produtor")], []), "p", "n" },
        { "PIPE_OPTIONAL_TO_REQUIRED", Pipeline([new("n", FieldType.Integer)], [Step("p", "produtor"), Step("c", "consumidor", ("valor", From.Step("p", "talvez")))], []), "c", "valor" },
        { "PIPE_CONST_INVALID", Pipeline([], [Step("p", "produtor", ("n", From.Constant("dez")))], []), "p", "n" },
        { "PIPE_OUTPUT_SOURCE", Pipeline([new("n", FieldType.Integer)], [Step("p", "produtor")], [new PipelineOutputDefinition("saida", From.Step("p", "inexistente"))]), "p", "saida" },
    };

    [Fact]
    public void Spec003_AC05_WarningsDoNotBlock()
    {
        var report = Validate(Pipeline(
            [new("n", FieldType.Integer), new("sobra", FieldType.String), new("anexo", FieldType.File)],
            [Step("p", "produtor")],
            []));

        report.IsValid.ShouldBeTrue();
        report.Warnings.Select(w => (w.Code, w.Field)).ShouldBe([("PIPE_UNUSED_INPUT", "sobra"), ("PIPE_UNUSED_INPUT", "anexo"), ("PIPE_FILE_INPUT", "anexo")]);
    }

    [Theory]
    [MemberData(nameof(Compatibility))]
    public void Spec003_AC06_TypeCompatibility(FieldType source, FieldType target, bool accepted)
    {
        TypeCompatibility.IsAssignable(new FieldDefinition("s", source), new FieldDefinition("t", target)).ShouldBe(accepted);
    }

    public static TheoryData<FieldType, FieldType, bool> Compatibility() => new()
    {
        { FieldType.Integer, FieldType.Number, true },
        { FieldType.Integer, FieldType.Decimal, true },
        { FieldType.Enum("a", "b"), FieldType.Enum("a", "b", "c"), true },
        { FieldType.Enum("a", "b"), FieldType.String, true },
        { FieldType.ListOf(FieldType.Integer), FieldType.ListOf(FieldType.Number), true },
        { FieldType.Table, FieldType.Json, true },
        { FieldType.Number, FieldType.Integer, false },
        { FieldType.String, FieldType.Enum("a"), false },
        { FieldType.Enum("a", "z"), FieldType.Enum("a", "b"), false },
        { FieldType.File, FieldType.Json, false },
        { FieldType.File, FieldType.ListOf(FieldType.File), false },
    };

    [Fact]
    public void TableColumns_MustExistInTheSource()
    {
        var source = new FieldDefinition("s", FieldType.Table) { Columns = [new TableColumn("i", FieldType.Integer), new TableColumn("x", FieldType.Number)] };
        var target = new FieldDefinition("t", FieldType.Table) { Columns = [new TableColumn("x", FieldType.Number)] };
        var missing = new FieldDefinition("t", FieldType.Table) { Columns = [new TableColumn("y", FieldType.Number)] };

        TypeCompatibility.IsAssignable(source, target).ShouldBeTrue();
        TypeCompatibility.IsAssignable(source, missing).ShouldBeFalse();
    }

    private static ValidationReport Validate(PipelineDefinition pipeline) =>
        PipelineRules.Validate(pipeline, (key, version) => (key, version) switch
        {
            ("produtor", 1) => Producer,
            ("consumidor", 1) => Consumer,
            _ => null,
        });

    private static StageDefinition Stage(string key, FieldDefinition[] inputs, FieldDefinition[]? outputs = null) => new()
    {
        Key = key,
        Version = 1,
        Name = key,
        Script = ScriptSource.Inline("x"),
        Inputs = inputs,
        Outputs = outputs ?? [],
    };

    private static PipelineStepDefinition Step(string key, string stage, params (string Input, BindingSource Source)[] bindings) =>
        new(key, stage, 1) { Bindings = [.. bindings.Select(b => new InputBinding(b.Input, b.Source))] };

    private static PipelineDefinition Pipeline(FieldDefinition[] inputs, PipelineStepDefinition[] steps, PipelineOutputDefinition[] outputs) => new()
    {
        Key = "trilha",
        Version = 1,
        Name = "Trilha",
        Inputs = inputs,
        Steps = steps,
        Outputs = outputs,
    };
}
