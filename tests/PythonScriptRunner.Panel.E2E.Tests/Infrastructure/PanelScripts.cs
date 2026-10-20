namespace PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

/// <summary>Scripts and inputs of the panel tests.</summary>
public static class PanelScripts
{
    /// <summary>Returns <c>texto</c> and prints it (stdout in the logs).</summary>
    public const string Echo = """
        from psrunner import stage


        @stage
        def run(inputs, ctx):
            print("eco:", inputs["texto"])
            return {"texto": inputs["texto"]}
        """;

    /// <summary>Fails while the file <c>marcador</c> does not exist (and creates it): the reprocess succeeds.</summary>
    public const string FailOnce = """
        import os

        from psrunner import StageError, stage


        @stage
        def run(inputs, ctx):
            if not os.path.exists(inputs["marcador"]):
                open(inputs["marcador"], "w").close()
                raise StageError("primeira tentativa falha", code="PRIMEIRA")
            return {"texto": "ok"}
        """;

    /// <summary>Inputs of <c>calculo-progressao</c>: terms 1, 3, 5 of y = x, so soma 9 and média 3.</summary>
    public static object Calculo { get; } = new { a1 = 1, razao = 2, n = 3, coeficientes = new[] { 0, 1 } };
}
