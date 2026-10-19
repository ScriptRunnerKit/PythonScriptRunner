using System.Text;
using System.Text.Json.Nodes;
using PythonScriptRunner.Core.Execution;
using PythonScriptRunner.Core.Tests.Infrastructure;

namespace PythonScriptRunner.Core.Tests.Integration;

public sealed class StageRunTests(PythonFixture fixture)
{
    [Fact]
    public async Task Spec001_AC01_FolderStage_EchoesInput()
    {
        // Arrange
        using var catalog = new TempCatalog();
        catalog.AddStage("eco", Scripts.EchoManifest, Scripts.Echo);
        await using var host = RunnerHost.Create(fixture, b => b.AddFileCatalog(catalog.Root));

        // Act
        await using var result = await host.Runner.RunStageAsync("eco", new { texto = "olá" }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Status.ShouldBe(RunStatus.Succeeded, result.Error?.Message);
        result.GetOutput<string>("texto").ShouldBe("olá");
    }

    [Fact]
    public async Task Spec001_AC02_InvalidInputs_StartNoProcess()
    {
        var executor = new CountingExecutor();
        await using var host = RunnerHost.Create(
            fixture,
            b => b.AddStage("soma", s => s.Script(Scripts.Echo).Input("n", FieldType.Integer)),
            services: s => s.AddSingleton<IPythonExecutor>(executor));

        await Should.ThrowAsync<InputValidationException>(() => host.Runner.RunStageAsync("soma", new { }));

        executor.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Spec001_AC03_EveryTypeSurvivesTheRoundTrip()
    {
        // Arrange
        FieldDefinition[] fields =
        [
            new("numero", FieldType.Number),
            new("inteiro", FieldType.Integer),
            new("valor", FieldType.Decimal),
            new("texto", FieldType.String),
            new("ativo", FieldType.Bool),
            new("dia", FieldType.Date),
            new("momento", FieldType.DateTime),
            new("tipo", FieldType.Enum("aritmetica", "geometrica")),
            new("dados", FieldType.Json),
            new("tabela", FieldType.Table),
            new("lista", FieldType.ListOf(FieldType.Number)),
            new("arquivo", FieldType.File),
            new("anexos", FieldType.ListOf(FieldType.File)),
        ];
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("espelho", s =>
        {
            s.Script(Scripts.EchoAllTypes);
            foreach (var field in fields)
            {
                s.Input(field).Output(field);
            }
        }));
        var moment = new DateTimeOffset(2026, 10, 19, 14, 30, 0, TimeSpan.FromHours(-3));
        Linha[] table = [new(1, 1.5), new(2, 2.5), new(3, 3.5)];

        // Act
        await using var result = await host.Runner.RunStageAsync("espelho", new
        {
            numero = 1.5,
            inteiro = 42L,
            valor = 0.1000000000000000000000000001m,
            texto = "olá",
            ativo = true,
            dia = new DateOnly(2026, 10, 19),
            momento = moment,
            tipo = "geometrica",
            dados = new { a = new[] { 1, 2 } },
            tabela = table,
            lista = new[] { 1.0, 2.5 },
            arquivo = PsrFile.FromText("<h1>oi</h1>", "pagina.html", "text/html"),
            anexos = new[] { PsrFile.FromText("A", "a.txt"), PsrFile.FromText("B", "b.txt") },
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Status.ShouldBe(RunStatus.Succeeded, result.Error?.Message);
        result.GetOutput<double>("numero").ShouldBe(1.5);
        result.GetOutput<long>("inteiro").ShouldBe(42);
        result.GetOutput<decimal>("valor").ShouldBe(0.1000000000000000000000000001m);
        result.GetOutput<string>("texto").ShouldBe("olá");
        result.GetOutput<bool>("ativo").ShouldBeTrue();
        result.GetOutput<DateOnly>("dia").ShouldBe(new DateOnly(2026, 10, 19));
        result.GetOutput<DateTimeOffset>("momento").ShouldBe(moment);
        result.GetOutput<DateTimeOffset>("momento").Offset.ShouldBe(TimeSpan.FromHours(-3));
        result.GetOutput<string>("tipo").ShouldBe("geometrica");
        result.GetOutput<JsonObject>("dados")["a"]!.ToJsonString().ShouldBe("[1,2]");
        result.GetOutput<List<Linha>>("tabela").ShouldBe(table);
        result.GetOutput<double[]>("lista").ShouldBe([1.0, 2.5]);
        (await ReadText(result.GetFile("arquivo"))).ShouldBe("<h1>oi</h1>");
        result.GetFiles("anexos").Select(f => f.FileName).ShouldBe(["0-a.txt", "1-b.txt"]);
    }

    [Fact]
    public async Task Spec001_AC04_UndeclaredOutput_IsContractViolation()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("extra", s => s
            .Script("""
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    return {"x": 1, "extra": 2}
                """)
            .Output("x", FieldType.Number)));

        await using var result = await host.Runner.RunStageAsync("extra", null, cancellationToken: TestContext.Current.CancellationToken);

        result.Status.ShouldBe(RunStatus.Failed);
        result.Error!.Kind.ShouldBe(ErrorKind.OutputContractViolation);
        result.Error.Message.ShouldContain("extra");
        result.Error.StepKey.ShouldBe("extra");
    }

    [Fact]
    public async Task Spec001_AC05_MissingRequiredOutput_IsContractViolation()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("falta", s => s
            .Script("""
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    return {}
                """)
            .Output("x", FieldType.Number)));

        await using var result = await host.Runner.RunStageAsync("falta", null, cancellationToken: TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.OutputContractViolation);
        result.Error.Message.ShouldContain("'x'");
    }

    [Fact]
    public async Task Spec001_AC06_StageError_KeepsCodeAndTraceback()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("falha", s => s.Script("""
            from psrunner import StageError, stage

            @stage
            def run(inputs, ctx):
                raise StageError("falhou", code="X1")
            """)));

        await using var result = await host.Runner.RunStageAsync("falha", null, cancellationToken: TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.ScriptError);
        result.Error.Code.ShouldBe("X1");
        result.Error.Message.ShouldBe("falhou");
        result.Error.Traceback.ShouldNotBeNull().ShouldContain("script.py");
        result.Steps[0].Attempts[0].ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Spec001_AC16_Files_ComeBackAndAreDeletedOnDispose()
    {
        // Arrange
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("maiusculas", s => s
            .Script("""
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    out = ctx.new_file("resultado.txt", "text/plain")
                    out.path.write_text(inputs["html"].read_text().upper(), encoding="utf-8")
                    return {"arquivo": out}
                """)
            .Input("html", FieldType.File)
            .Output("arquivo", FieldType.File)));

        // Act
        var result = await host.Runner.RunStageAsync("maiusculas", new { html = PsrFile.FromText("<p>oi</p>", "pagina.html", "text/html") }, cancellationToken: TestContext.Current.CancellationToken);
        var file = result.GetFile("arquivo");
        var content = await ReadText(file);
        await result.DisposeAsync();

        // Assert
        content.ShouldBe("<P>OI</P>");
        file.FileName.ShouldBe("resultado.txt");
        file.ContentType.ShouldBe("text/plain");
        file.Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("<P>OI</P>"))));
        File.Exists(file.LocalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Spec001_AC17_FileOverTheLimit_IsArtifactLimitExceeded()
    {
        await using var host = RunnerHost.Create(
            fixture,
            b => b.AddStage("grande", s => s
                .Script("""
                    from psrunner import stage

                    @stage
                    def run(inputs, ctx):
                        out = ctx.new_file("grande.bin")
                        out.path.write_bytes(b"0" * (2 * 1024 * 1024))
                        return {"arquivo": out}
                    """)
                .Output("arquivo", FieldType.File)),
            o => o.MaxFileBytes = 1024 * 1024);

        await using var result = await host.Runner.RunStageAsync("grande", null, cancellationToken: TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.ArtifactLimitExceeded);
        result.Error.Code.ShouldBe("FILE_TOO_LARGE");
    }

    [Fact]
    public async Task Spec001_AC19_LargeStdout_KeepsTheLastMegabyte()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("verboso", s => s.Script("""
            import sys
            from psrunner import stage

            @stage
            def run(inputs, ctx):
                sys.stdout.write("x" * 3_000_000)
                sys.stdout.flush()
                return {}
            """)));

        await using var result = await host.Runner.RunStageAsync("verboso", null, cancellationToken: TestContext.Current.CancellationToken);

        var stdout = result.Steps[0].Attempts[0].Stdout;
        stdout.Content.Length.ShouldBe(1024 * 1024);
        stdout.Truncated.ShouldBeTrue();
        stdout.OriginalBytes.ShouldBeGreaterThanOrEqualTo(3_000_000);
    }

    [Fact]
    public async Task Spec001_AC20_EachStageUsesItsPythonVersion()
    {
        const string script = """
            import sys
            from psrunner import stage

            @stage
            def run(inputs, ctx):
                return {"versao": f"{sys.version_info[0]}.{sys.version_info[1]}"}
            """;
        await using var host = RunnerHost.Create(fixture, b => b
            .AddStage("py311", s => s.Python("3.11").Script(script).Output("versao", FieldType.String))
            .AddStage("py312", s => s.Python("3.12").Script(script).Output("versao", FieldType.String)));

        await using var older = await host.Runner.RunStageAsync("py311", null, cancellationToken: TestContext.Current.CancellationToken);
        await using var newer = await host.Runner.RunStageAsync("py312", null, cancellationToken: TestContext.Current.CancellationToken);

        older.GetOutput<string>("versao").ShouldBe("3.11", older.Error?.Message);
        newer.GetOutput<string>("versao").ShouldBe("3.12", newer.Error?.Message);
    }

    [Fact]
    public async Task Spec001_AC23_CoreAlone_NeedsNoDatabaseOrQueue()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("eco", s => s.Script(Scripts.Echo).Input("texto", FieldType.String).Output("texto", FieldType.String)));

        await using var result = await host.Runner.RunStageAsync("eco", new { texto = "sem banco" }, cancellationToken: TestContext.Current.CancellationToken);

        result.GetOutput<string>("texto").ShouldBe("sem banco");
        host.Services.GetRequiredService<IRunRecorder>().GetType().Name.ShouldBe("NullRunRecorder");
    }

    [Fact]
    public async Task Spec001_AC25_FluentAndFolderStagesBehaveTheSame()
    {
        using var catalog = new TempCatalog();
        catalog.AddStage("eco", Scripts.EchoManifest, Scripts.Echo);
        await using var folder = RunnerHost.Create(fixture, b => b.AddFileCatalog(catalog.Root));
        await using var fluent = RunnerHost.Create(fixture, b => b.AddStage("eco", s => s.Script(Scripts.Echo).Input("texto", FieldType.String).Output("texto", FieldType.String)));

        await using var fromFolder = await folder.Runner.RunStageAsync("eco", new { texto = "igual" }, cancellationToken: TestContext.Current.CancellationToken);
        await using var fromCode = await fluent.Runner.RunStageAsync("eco", new { texto = "igual" }, cancellationToken: TestContext.Current.CancellationToken);

        fromFolder.Outputs.ToJsonString().ShouldBe(fromCode.Outputs.ToJsonString());
        fromFolder.Status.ShouldBe(fromCode.Status);
    }

    private static async Task<string> ReadText(PsrFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public sealed record Linha(long I, double X);

    private sealed class CountingExecutor : IPythonExecutor
    {
        public int Calls { get; private set; }

        public Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ExecutionResult { Status = AttemptStatus.Succeeded, Outputs = [] });
        }
    }
}
