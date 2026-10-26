using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>Builds dataset documents for handlers. The store fills in the reference when it retains one.</summary>
public static class Datasets
{
    public static JsonObject Create(
        string title,
        IReadOnlyList<DatasetColumn> columns,
        IEnumerable<JsonObject> rows,
        DatasetProvenance provenance) => new()
        {
            ["documentType"] = "dataset",
            ["schemaVersion"] = CompositionValidator.SchemaVersion,
            ["title"] = title,
            ["columns"] = JsonSerializer.SerializeToNode(columns, WireJson.Options),
            ["rows"] = new JsonArray([.. rows]),
            ["provenance"] = JsonSerializer.SerializeToNode(provenance, WireJson.Options),
        };
}
