using System.Text.Json.Nodes;
using PythonScriptRunner.Core.Execution;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Tests.Values;

public sealed class OutputContractTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "psr-tests", "output", Guid.NewGuid().ToString("N"));

    public OutputContractTests() => Directory.CreateDirectory(Path.Combine(_work, "out"));

    [Fact]
    public void Spec001_AC18_OutputFileOutsideOut_IsContractViolation()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_work, "segredo.txt"), "x");
        var request = Request([new FieldDefinition("arquivo", FieldType.File)]);
        WriteOutput("""{"arquivo":{"$file":{"path":"out/../segredo.txt","name":"segredo.txt"}}}""");

        // Act
        var result = OutputReader.Read(request, 0, TimeSpan.Zero, LogTail.Empty, LogTail.Empty);

        // Assert
        result.Status.ShouldBe(AttemptStatus.Failed);
        result.Error!.Kind.ShouldBe(ErrorKind.OutputContractViolation);
        result.Error.Code.ShouldBe("OUTPUT_FILE_OUTSIDE_OUT");
    }

    [Fact]
    public void Read_UndeclaredOutput_IsContractViolation()
    {
        var request = Request([new FieldDefinition("x", FieldType.Number)]);
        WriteOutput("""{"x":1,"extra":2}""");

        var result = OutputReader.Read(request, 0, TimeSpan.Zero, LogTail.Empty, LogTail.Empty);

        result.Error!.Code.ShouldBe("UNDECLARED_OUTPUT");
        result.Error.Message.ShouldContain("extra");
    }

    [Fact]
    public void Read_MissingOutputJson_IsProcessCrashed()
    {
        var request = Request([]);

        var result = OutputReader.Read(request, 3, TimeSpan.Zero, LogTail.Empty, LogTail.Empty);

        result.Error!.Kind.ShouldBe(ErrorKind.ProcessCrashed);
        result.ExitCode.ShouldBe(3);
    }

    [Fact]
    public void Read_ValidFile_ReturnsMetadataWithHash()
    {
        File.WriteAllText(Path.Combine(_work, "out", "a.txt"), "abc");
        var request = Request([new FieldDefinition("a", FieldType.File)]);
        WriteOutput("""{"a":{"$file":{"path":"out/a.txt","name":"a.txt","contentType":"text/plain"}}}""");

        var result = OutputReader.Read(request, 0, TimeSpan.Zero, LogTail.Empty, LogTail.Empty);

        result.Status.ShouldBe(AttemptStatus.Succeeded);
        var file = result.Files.ShouldHaveSingleItem();
        file.Sha256.ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        result.Outputs!["a"]!["$file"]!["size"]!.GetValue<long>().ShouldBe(3);
    }

    [Theory]
    [InlineData("decimal", "\"12.5\"", true)]
    [InlineData("decimal", "12.5", false)]
    [InlineData("datetime", "\"2026-10-19T10:00:00-03:00\"", true)]
    [InlineData("datetime", "\"2026-10-19T10:00:00Z\"", true)]
    [InlineData("datetime", "\"2026-10-19T10:00:00\"", false)]
    [InlineData("date", "\"2026-10-19\"", true)]
    [InlineData("integer", "1.5", false)]
    [InlineData("number", "1", true)]
    public void WireValidator_ChecksScalars(string type, string json, bool valid)
    {
        var errors = new List<WireError>();

        WireValidator.Validate(JsonNode.Parse(json), new FieldDefinition("x", FieldType.Parse(type)), "x", errors);

        (errors.Count == 0).ShouldBe(valid);
    }

    [Fact]
    public void WireValidator_ChecksTableColumns()
    {
        var field = new FieldDefinition("t", FieldType.Table) { Columns = [new TableColumn("i", FieldType.Integer)] };
        var errors = new List<WireError>();

        WireValidator.Validate(JsonNode.Parse("""[{"i":1},{"j":2}]"""), field, "t", errors);

        errors.ShouldHaveSingleItem().Code.ShouldBe("MISSING_COLUMN");
    }

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private void WriteOutput(string outputs) =>
        File.WriteAllText(Path.Combine(_work, "output.json"), $$"""{"protocol":1,"status":"ok","outputs":{{outputs}}}""");

    private ExecutionRequest Request(FieldDefinition[] outputs) => new()
    {
        Stage = new StageDefinition { Key = "test", Version = 1, Name = "test", Script = ScriptSource.Inline("x"), Outputs = outputs },
        Environment = new PythonEnvironment("hash", _work, "python", false),
        RunId = RunId.New(),
        StepKey = "test",
        Attempt = 1,
        WorkDirectory = _work,
        Inputs = [],
        Timeout = TimeSpan.FromMinutes(1),
        MemoryLimitMb = 256,
        MaxFileBytes = 1024,
        FileBudgetBytes = 4096,
        MaxLogBytes = 1024,
    };
}
