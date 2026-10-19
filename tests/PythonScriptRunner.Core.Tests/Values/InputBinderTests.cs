using System.Text.Json.Nodes;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Tests.Values;

public sealed class InputBinderTests
{
    private const long MaxFile = 1024 * 1024;

    [Fact]
    public void Spec001_AC02_MissingRequiredInput_ThrowsInputValidationException()
    {
        // Arrange
        FieldDefinition[] fields = [new("n", FieldType.Integer)];

        // Act
        var exception = Should.Throw<InputValidationException>(() => InputBinder.Bind(fields, new { }, MaxFile));

        // Assert
        exception.Errors.ShouldHaveSingleItem().ShouldBe(new InputError("n", "MISSING_REQUIRED_INPUT", "required input is missing"));
    }

    [Fact]
    public void Bind_WithUndeclaredInput_ReportsIt()
    {
        FieldDefinition[] fields = [new("a", FieldType.Number)];

        var exception = Should.Throw<InputValidationException>(() => InputBinder.Bind(fields, new { a = 1.0, b = 2 }, MaxFile));

        exception.Errors.ShouldHaveSingleItem().Code.ShouldBe("UNDECLARED_INPUT");
    }

    [Fact]
    public void Bind_ConvertsDotNetValuesToWireFormat()
    {
        // Arrange
        FieldDefinition[] fields =
        [
            new("numero", FieldType.Number),
            new("inteiro", FieldType.Integer),
            new("valor", FieldType.Decimal),
            new("dia", FieldType.Date),
            new("momento", FieldType.DateTime),
            new("tipo", FieldType.Enum("aritmetica", "geometrica")),
            new("tabela", FieldType.Table),
            new("lista", FieldType.ListOf(FieldType.Integer)),
        ];

        // Act
        var bound = InputBinder.Bind(fields, new
        {
            numero = 3,
            inteiro = 42,
            valor = 12.50m,
            dia = new DateOnly(2026, 10, 19),
            momento = new DateTimeOffset(2026, 10, 19, 14, 30, 0, TimeSpan.FromHours(-3)),
            tipo = Tipo.Geometrica,
            tabela = new[] { new Linha(1, 2.5) },
            lista = new List<int> { 1, 2 },
        }, MaxFile);

        // Assert
        var wire = bound.Wire;
        wire["numero"]!.GetValue<double>().ShouldBe(3.0);
        wire["inteiro"]!.GetValue<long>().ShouldBe(42);
        wire["valor"]!.GetValue<string>().ShouldBe("12.50");
        wire["dia"]!.GetValue<string>().ShouldBe("2026-10-19");
        wire["momento"]!.GetValue<string>().ShouldBe("2026-10-19T14:30:00.0000000-03:00");
        wire["tipo"]!.GetValue<string>().ShouldBe("geometrica");
        wire["tabela"]!.ToJsonString().ShouldBe("""[{"i":1,"x":2.5}]""");
        wire["lista"]!.ToJsonString().ShouldBe("[1,2]");
    }

    [Theory]
    [InlineData("number", "texto")]
    [InlineData("integer", 1.5)]
    [InlineData("decimal", 0.1)]
    [InlineData("bool", 1)]
    public void Bind_RejectsWrongTypes(string type, object value)
    {
        FieldDefinition[] fields = [new("x", FieldType.Parse(type))];

        var exception = Should.Throw<InputValidationException>(() => InputBinder.Bind(fields, new Dictionary<string, object?> { ["x"] = value }, MaxFile));

        exception.Errors.ShouldHaveSingleItem().Code.ShouldBe("TYPE_MISMATCH");
    }

    [Fact]
    public void Bind_RejectsUnspecifiedDateTime_AndEnumOutsideValues()
    {
        FieldDefinition[] fields = [new("momento", FieldType.DateTime), new("tipo", FieldType.Enum("a"))];

        var exception = Should.Throw<InputValidationException>(() => InputBinder.Bind(fields, new { momento = new DateTime(2026, 1, 1), tipo = "b" }, MaxFile));

        exception.Errors.Select(e => e.Code).ShouldBe(["NAIVE_DATETIME", "ENUM_VALUE_INVALID"]);
    }

    [Fact]
    public void Bind_AppliesDefaults_AndSkipsAbsentOptionalInputs()
    {
        FieldDefinition[] fields =
        [
            new("tipo", FieldType.Enum("a", "b")) { Required = false, Default = "b" },
            new("anexo", FieldType.File) { Required = false },
        ];

        var bound = InputBinder.Bind(fields, null, MaxFile);

        bound.Wire.ToJsonString().ShouldBe("""{"tipo":"b"}""");
        bound.Files.ShouldBeEmpty();
    }

    [Fact]
    public void Bind_Files_ChecksContentTypeAndSize()
    {
        FieldDefinition[] fields = [new("html", FieldType.File) { ContentTypes = ["text/html"] }];

        var exception = Should.Throw<InputValidationException>(() =>
            InputBinder.Bind(fields, new { html = PsrFile.FromBytes(new byte[MaxFile + 1], "a.pdf", "application/pdf") }, MaxFile));

        exception.Errors.Select(e => e.Code).ShouldBe(["CONTENT_TYPE_NOT_ALLOWED", "FILE_TOO_LARGE"]);
    }

    [Fact]
    public void Bind_FileList_KeepsOrderAndMetadata()
    {
        FieldDefinition[] fields = [new("anexos", FieldType.ListOf(FieldType.File))];

        var bound = InputBinder.Bind(fields, new { anexos = new[] { PsrFile.FromText("a", "a.txt"), PsrFile.FromText("b", "b.txt") } }, MaxFile);

        bound.Files.Select(f => (f.Field, f.Index, f.File.FileName)).ShouldBe([("anexos", 0, "a.txt"), ("anexos", 1, "b.txt")]);
        bound.Wire["anexos"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Bind_AcceptsJsonObjectsAndRecordsWithPascalCaseProperties()
    {
        FieldDefinition[] fields = [new("nome_arquivo", FieldType.String), new("a1", FieldType.Number)];

        var fromRecord = InputBinder.Bind(fields, new Entrada("relatorio.pdf", 1), MaxFile);
        var fromJson = InputBinder.Bind(fields, new JsonObject { ["nome_arquivo"] = "relatorio.pdf", ["a1"] = 1 }, MaxFile);

        fromRecord.Wire.ToJsonString().ShouldBe(fromJson.Wire.ToJsonString());
    }

    private enum Tipo
    {
        Aritmetica,
        Geometrica,
    }

    private sealed record Linha(int I, double X);

    private sealed record Entrada(string NomeArquivo, double A1);
}
