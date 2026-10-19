using PythonScriptRunner.Core.Tests.Infrastructure;

namespace PythonScriptRunner.Core.Tests.Integration;

public sealed class PipelineRunTests(PythonFixture fixture)
{
    private const string Generator = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            out = ctx.new_file(inputs["nome"], "text/plain")
            out.path.write_text(inputs["texto"], encoding="utf-8")
            return {"arquivo": out, "tamanho": len(inputs["texto"])}
        """;

    private const string Reader = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            conteudo = inputs["arquivo"].read_text()
            return {"resumo": f"{inputs['prefixo']}: {conteudo}", "porta": inputs["porta"]}
        """;

    private const string Ok = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            return {"valor": inputs["valor"] + 1}
        """;

    private string CalculoProgressao => Path.Combine(fixture.RepositoryRoot, "tests", "fixtures", "calculo-progressao");

    [Fact]
    public async Task Spec003_AC01_CalculoProgressao_ProducesTheExpectedNumbers()
    {
        // Arrange
        await using var host = RunnerHost.Create(fixture, b => b.AddFileCatalog(CalculoProgressao));

        // Act
        await using var result = await host.Runner.RunPipelineAsync("calculo-progressao", new
        {
            a1 = 1,
            razao = 2,
            n = 4,
            tipo = "geometrica",
            coeficientes = new[] { 1.0, 0.0, 1.0 },
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Status.ShouldBe(RunStatus.Succeeded, result.Error?.Message);
        result.Kind.ShouldBe(RunKind.Pipeline);
        result.GetOutput<double>("soma").ShouldBe(89);
        result.GetOutput<double>("media").ShouldBe(22.25);
        result.GetOutput<double>("desvio_padrao").ShouldBe(25.3118, 1e-4);
        var tabela = result.GetOutput<List<Linha>>("tabela");
        tabela.ShouldBe([new(1, 1, 2), new(2, 2, 5), new(3, 4, 17), new(4, 8, 65)]);
        result.Steps.Select(s => (s.StepKey, s.Status)).ShouldBe(
        [
            ("termos", StepStatus.Succeeded),
            ("polinomio", StepStatus.Succeeded),
            ("estatisticas", StepStatus.Succeeded),
            ("tabela", StepStatus.Succeeded),
        ]);
    }

    [Fact]
    public async Task Spec003_AC02_StepCanReadAnyEarlierStep()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddFileCatalog(CalculoProgressao));

        await using var result = await host.Runner.RunPipelineAsync("calculo-progressao", new
        {
            a1 = 3,
            razao = 2,
            n = 3,
            coeficientes = new[] { 0.0, 1.0 },
        }, cancellationToken: TestContext.Current.CancellationToken);

        // 'tabela' (step 4) reads 'termos' from step 1, skipping steps 2 and 3; tipo uses its default (aritmetica).
        result.GetStepOutput<double[]>("termos", "termos").ShouldBe([3, 5, 7]);
        result.GetStepOutput<List<Linha>>("tabela", "tabela").Select(l => l.X).ShouldBe([3, 5, 7]);
    }

    [Fact]
    public async Task Spec003_AC03_AC04_AC09_ConstantsImplicitBindingsAndFilesBetweenSteps()
    {
        // Arrange
        await using var host = RunnerHost.Create(fixture, b => b
            .AddStage("gerar", s => s.Script(Generator)
                .Input("texto", FieldType.String).Input("nome", FieldType.String)
                .Output("arquivo", FieldType.File).Output("tamanho", FieldType.Integer))
            .AddStage("ler", s => s.Script(Reader)
                .Input("arquivo", FieldType.File).Input("prefixo", FieldType.String).Input("porta", FieldType.Integer)
                .Output("resumo", FieldType.String).Output("porta", FieldType.Integer))
            .AddPipeline("arquivo-entre-passos", p => p
                .Input("texto", FieldType.String)
                .Input("prefixo", FieldType.String)
                .Step("gerar", "gerar@1", s => s.Bind("nome", From.Constant("dados.txt")))
                .Step("ler", "ler@1", s => s
                    .Bind("prefixo", From.Pipeline("prefixo"))
                    .Bind("porta", From.Constant(465)))
                .Output("arquivo", From.Step("gerar", "arquivo"))
                .Output("resumo", From.Step("ler", "resumo"))
                .Output("porta", From.Step("ler", "porta"))));

        // Act
        await using var result = await host.Runner.RunPipelineAsync("arquivo-entre-passos", new { texto = "olá", prefixo = "conteúdo" },
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Status.ShouldBe(RunStatus.Succeeded, result.Error?.Message);
        result.GetOutput<int>("porta").ShouldBe(465);
        result.GetOutput<string>("resumo").ShouldBe("conteúdo: olá");
        var file = result.GetFile("arquivo");
        file.FileName.ShouldBe("dados.txt");
        File.ReadAllText(file.LocalPath!).ShouldBe("olá");
        Should.Throw<KeyNotFoundException>(() => result.GetOutput<int>("tamanho"));
    }

    [Fact]
    public async Task Spec003_AC07_FailedStep_SkipsTheRest()
    {
        await using var host = RunnerHost.Create(fixture, b => WithChain(b, failing: """
            from psrunner import StageError, stage

            @stage
            def run(inputs, ctx):
                raise StageError("divergiu", code="DIV")
            """));

        await using var result = await host.Runner.RunPipelineAsync("cadeia", new { valor = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        result.Status.ShouldBe(RunStatus.Failed);
        result.Steps.Select(s => s.Status).ShouldBe([StepStatus.Succeeded, StepStatus.Failed, StepStatus.Skipped, StepStatus.Skipped]);
        result.Error!.StepKey.ShouldBe("polinomio");
        result.Error.Code.ShouldBe("DIV");
        result.Outputs.ShouldBeEmpty();
        result.GetStepOutput<int>("termos", "valor").ShouldBe(2);
    }

    [Fact]
    public async Task Spec003_AC08_RetryInsideAStep_CompletesThePipeline()
    {
        var counter = Path.Combine(Path.GetTempPath(), "psr-tests", $"counter-{Guid.NewGuid():N}.txt");
        await using var host = RunnerHost.Create(fixture, b => WithChain(b, failing: $$"""
            from pathlib import Path
            from psrunner import StageError, stage

            @stage
            def run(inputs, ctx):
                counter = Path(r"{{counter}}")
                count = int(counter.read_text()) + 1 if counter.exists() else 1
                counter.write_text(str(count))
                if count == 1:
                    raise StageError("falha transitória")
                return {"valor": inputs["valor"] + 1}
            """, retries: 1));

        await using var result = await host.Runner.RunPipelineAsync("cadeia", new { valor = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        result.Status.ShouldBe(RunStatus.Succeeded, result.Error?.Message);
        result.Steps[1].Attempts.Count.ShouldBe(2);
        result.GetOutput<int>("valor").ShouldBe(5);
    }

    [Fact]
    public async Task Spec003_AC11_Cancellation_StopsTheStepAndSkipsTheRest()
    {
        await using var host = RunnerHost.Create(fixture, b => WithChain(b, failing: """
            import time
            from psrunner import stage

            @stage
            def run(inputs, ctx):
                time.sleep(60)
                return {"valor": 0}
            """));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));

        await using var result = await host.Runner.RunPipelineAsync("cadeia", new { valor = 1 }, cancellationToken: cancellation.Token);

        result.Status.ShouldBe(RunStatus.Cancelled);
        result.Steps.Select(s => s.Status).ShouldBe([StepStatus.Succeeded, StepStatus.Cancelled, StepStatus.Skipped, StepStatus.Skipped]);
    }

    [Fact]
    public async Task Spec003_AC12_FolderPipeline_WithMissingStageVersion_FailsToLoad()
    {
        // Arrange
        using var catalog = new TempCatalog();
        catalog.AddStage("eco", Scripts.EchoManifest, Scripts.Echo);
        var pipelines = Path.Combine(catalog.Root, "pipelines");
        Directory.CreateDirectory(pipelines);
        await File.WriteAllTextAsync(Path.Combine(pipelines, "quebrada.pipeline.json"), """
            {
              "key": "quebrada", "version": 1, "name": "Quebrada",
              "inputs": [ { "name": "texto", "type": "string" } ],
              "steps": [ { "key": "eco", "stage": "eco@2" } ]
            }
            """, TestContext.Current.CancellationToken);
        await using var host = RunnerHost.Create(fixture, b => b.AddFileCatalog(catalog.Root));

        // Act
        var exception = await Should.ThrowAsync<PsrConfigurationException>(() =>
            host.Runner.RunPipelineAsync("quebrada", new { texto = "x" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("PIPE_STAGE_NOT_PUBLISHED");
        exception.Message.ShouldContain("eco@2");
    }

    [Fact]
    public async Task Spec003_AC13_FluentAndFilePipelines_BehaveTheSame()
    {
        // Arrange: the same stages, with the pipeline from the JSON file or from C#.
        using var stagesOnly = new TempCatalog();
        CopyDirectory(Path.Combine(CalculoProgressao, "stages"), Path.Combine(stagesOnly.Root, "stages"));
        await using var fromFile = RunnerHost.Create(fixture, b => b.AddFileCatalog(CalculoProgressao));
        await using var fromCode = RunnerHost.Create(fixture, b => b
            .AddFileCatalog(stagesOnly.Root)
            .AddPipeline("calculo-progressao", p => p
                .Name("Cálculo de progressão")
                .Input("a1", FieldType.Number)
                .Input("razao", FieldType.Number)
                .Input("n", FieldType.Integer)
                .Input("tipo", FieldType.Enum("aritmetica", "geometrica"), required: false, @default: "aritmetica")
                .Input("coeficientes", FieldType.ListOf(FieldType.Number))
                .Step("termos", "progressao-termos@1")
                .Step("polinomio", "polinomio-avaliar@1", s => s
                    .Bind("coeficientes", From.Pipeline("coeficientes"))
                    .Bind("x", From.Step("termos", "termos")))
                .Step("estatisticas", "estatisticas@1", s => s.Bind("valores", From.Step("polinomio", "valores")))
                .Step("tabela", "montar-tabela@1", s => s
                    .Bind("x", From.Step("termos", "termos"))
                    .Bind("y", From.Step("polinomio", "valores")))
                .Output("soma", From.Step("estatisticas", "soma"))
                .Output("media", From.Step("estatisticas", "media"))
                .Output("desvio_padrao", From.Step("estatisticas", "desvio_padrao"))
                .Output("tabela", From.Step("tabela", "tabela"))));
        var inputs = new { a1 = 2, razao = 3, n = 5, tipo = "aritmetica", coeficientes = new[] { 1.0, 1.0 } };

        // Act
        var fileReport = await Validate(fromFile);
        var codeReport = await Validate(fromCode);
        await using var fileResult = await fromFile.Runner.RunPipelineAsync("calculo-progressao", inputs, cancellationToken: TestContext.Current.CancellationToken);
        await using var codeResult = await fromCode.Runner.RunPipelineAsync("calculo-progressao", inputs, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        fileReport.Errors.ShouldBe(codeReport.Errors);
        fileReport.Warnings.ShouldBe(codeReport.Warnings);
        fileReport.IsValid.ShouldBeTrue();
        fileResult.Status.ShouldBe(RunStatus.Succeeded, fileResult.Error?.Message);
        fileResult.Outputs.ToJsonString().ShouldBe(codeResult.Outputs.ToJsonString());
    }

    [Fact]
    public async Task IntegerOutput_FeedsADecimalInput_AsAnExactDecimal()
    {
        await using var host = RunnerHost.Create(fixture, b => b
            .AddStage("contar", s => s.Script("""
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    return {"quantidade": 5}
                """).Output("quantidade", FieldType.Integer))
            .AddStage("preco", s => s.Script("""
                from decimal import Decimal
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    assert isinstance(inputs["quantidade"], Decimal)
                    return {"total": inputs["quantidade"] * Decimal("0.10")}
                """).Input("quantidade", FieldType.Decimal).Output("total", FieldType.Decimal))
            .AddPipeline("venda", p => p.Step("contar", "contar@1").Step("preco", "preco@1").Output("total", From.Step("preco", "total"))));

        await using var result = await host.Runner.RunPipelineAsync("venda", null, cancellationToken: TestContext.Current.CancellationToken);

        result.Status.ShouldBe(RunStatus.Succeeded, result.Error?.Message);
        result.GetOutput<decimal>("total").ShouldBe(0.50m);
    }

    [Fact]
    public async Task UnknownPipeline_AndInvalidInputs_AreReportedBeforeRunning()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddFileCatalog(CalculoProgressao));

        await Should.ThrowAsync<PipelineNotFoundException>(() => host.Runner.RunPipelineAsync("inexistente", null, cancellationToken: TestContext.Current.CancellationToken));
        var invalid = await Should.ThrowAsync<InputValidationException>(() =>
            host.Runner.RunPipelineAsync("calculo-progressao", new { a1 = 1 }, cancellationToken: TestContext.Current.CancellationToken));
        invalid.Errors.Select(e => e.Field).ShouldBe(["razao", "n", "coeficientes"]);
    }

    private static IPythonScriptRunnerBuilder WithChain(IPythonScriptRunnerBuilder builder, string failing, int retries = 0) => builder
        .AddStage("incremento", s => s.Script(Ok).Input("valor", FieldType.Integer).Output("valor", FieldType.Integer))
        .AddStage("instavel", s => s.Script(failing).Input("valor", FieldType.Integer).Output("valor", FieldType.Integer)
            .Retry(retries, TimeSpan.FromMilliseconds(50)))
        .AddPipeline("cadeia", p => p
            .Input("valor", FieldType.Integer)
            .Step("termos", "incremento@1")
            .Step("polinomio", "instavel@1")
            .Step("estatisticas", "incremento@1")
            .Step("tabela", "incremento@1")
            .Output("valor", From.Step("tabela", "valor")));

    private static async Task<ValidationReport> Validate(RunnerHost host)
    {
        var catalog = host.Services.GetRequiredService<ICatalog>();
        var pipeline = await catalog.FindPipelineAsync("calculo-progressao", null, TestContext.Current.CancellationToken);
        return await host.Services.GetRequiredService<IPipelineValidator>().ValidateAsync(pipeline!, TestContext.Current.CancellationToken);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    public sealed record Linha(long I, double X, double Y);
}
