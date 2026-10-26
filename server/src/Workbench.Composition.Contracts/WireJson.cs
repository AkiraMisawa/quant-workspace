using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench.Composition.Contracts;

/// <summary>
/// The one set of serializer options for the wire vocabulary. Unknown members are refused rather than
/// dropped, so a definition carrying something this contract does not know (a script, an expression)
/// fails instead of running without it.
/// </summary>
public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
