namespace PythonScriptRunner.Core.Tests.Infrastructure;

/// <summary>Python scripts shared by the tests.</summary>
public static class Scripts
{
    public const string Echo = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            return {"texto": inputs["texto"]}
        """;

    public const string EchoManifest = """
        {
          "key": "eco",
          "version": 1,
          "name": "Eco",
          "inputs": [ { "name": "texto", "type": "string" } ],
          "outputs": [ { "name": "texto", "type": "string" } ]
        }
        """;

    public const string EchoAllTypes = """
        import shutil
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            out = {k: v for k, v in inputs.items() if k not in ("arquivo", "anexos")}
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
            return out
        """;
}
