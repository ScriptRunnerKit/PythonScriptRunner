using System.Text.Json;
using System.Text.Json.Serialization;

namespace PythonScriptRunner;

/// <summary>JSON settings used to map .NET values to the wire format and back.</summary>
/// <remarks>
/// Property names use snake_case (<c>DesvioPadrao</c> ↔ <c>desvio_padrao</c>), numbers may be read from
/// strings (decimals travel as strings) and enums are written in snake_case.
/// </remarks>
public static class PsrJson
{
    /// <summary>Shared options (do not modify).</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
