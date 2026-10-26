using System.Text.Json.Nodes;

namespace Workbench.Composition.Application;

/// <summary>
/// Checks a small form-sized value against its contract's schema. Only a declared subset of JSON Schema
/// is understood; a schema using anything else is refused at registration rather than half-checked.
/// </summary>
public static class ContractChecks
{
    static readonly HashSet<string> ObjectKeywords = ["type", "properties", "required", "additionalProperties", "title"];
    static readonly HashSet<string> FieldKeywords = ["type", "title", "description", "enum", "minimum", "maximum", "minLength", "format", "default"];
    static readonly HashSet<string> FieldTypes = ["string", "integer", "number", "boolean"];

    public static IEnumerable<string> SchemaProblems(ContractDefinition contract)
    {
        var schema = contract.Schema;
        foreach (var keyword in schema.Select(p => p.Key).Where(k => !ObjectKeywords.Contains(k)))
            yield return $"{contract.ContractId}: keyword '{keyword}' is not supported on the object.";
        if (schema["type"]?.GetValue<string>() != "object")
            yield return $"{contract.ContractId}: a form contract is an object.";
        if (schema["additionalProperties"]?.GetValue<bool>() != false)
            yield return $"{contract.ContractId}: a form contract refuses additional properties.";
        foreach (var (name, field) in schema["properties"]?.AsObject() ?? [])
        {
            foreach (var keyword in field!.AsObject().Select(p => p.Key).Where(k => !FieldKeywords.Contains(k)))
                yield return $"{contract.ContractId}.{name}: keyword '{keyword}' is not supported.";
            if (!FieldTypes.Contains(field["type"]?.GetValue<string>() ?? ""))
                yield return $"{contract.ContractId}.{name}: type must be one of {string.Join(", ", FieldTypes)}.";
        }
    }

    public static IEnumerable<CompositionIssue> Check(ContractDefinition contract, JsonObject value, string at)
    {
        var properties = contract.Schema["properties"]?.AsObject() ?? [];
        foreach (var required in contract.Schema["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [])
            if (!value.ContainsKey(required))
                yield return new("Required", $"{at}/{required}", $"{required} is required.");
        foreach (var (name, node) in value)
        {
            if (!properties.TryGetPropertyValue(name, out var field) || field is null)
            {
                yield return new("UnknownField", $"{at}/{name}", $"{contract.ContractId} has no field {name}.");
                continue;
            }
            if (FieldProblem(field.AsObject(), node) is { } problem)
                yield return new("InvalidField", $"{at}/{name}", problem);
        }
    }

    static string? FieldProblem(JsonObject field, JsonNode? node)
    {
        if (node is not JsonValue value) return "A value is required.";
        var type = field["type"]!.GetValue<string>();
        switch (type)
        {
            case "string":
                if (value.GetValueKind() != System.Text.Json.JsonValueKind.String) return "A text value is required.";
                var text = value.GetValue<string>();
                if (field["minLength"] is { } min && text.Length < min.GetValue<int>()) return $"At least {min} characters are required.";
                if (field["enum"] is JsonArray options && options.All(o => o!.GetValue<string>() != text))
                    return $"Must be one of {string.Join(", ", options)}.";
                if (field["format"]?.GetValue<string>() == "date" && !DateOnly.TryParseExact(text, "yyyy-MM-dd", out _))
                    return "A date as yyyy-MM-dd is required.";
                return null;
            case "integer":
            case "number":
                if (value.GetValueKind() != System.Text.Json.JsonValueKind.Number) return "A number is required.";
                var number = decimal.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
                if (type == "integer" && number != decimal.Truncate(number)) return "A whole number is required.";
                if (field["minimum"] is { } lo && number < lo.GetValue<decimal>()) return $"Must be at least {lo}.";
                if (field["maximum"] is { } hi && number > hi.GetValue<decimal>()) return $"Must be at most {hi}.";
                return null;
            case "boolean":
                return value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? null : "true or false is required.";
            default:
                return $"Unsupported type {type}.";
        }
    }
}
