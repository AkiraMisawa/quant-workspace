using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Synthetic;

// Non-financial synthetic capabilities that prove the kernel is domain independent (ADR-0002, CO01).
// Every value is generated, deterministic and labelled synthetic; none stands for real data.

static class Contracts
{
    public const string Version = "1.0.0";
    public const string Region = "sample.region";
    public const string StationId = "sample.station-id";
    public const string Station = "sample.station";
    public const string Stations = "sample.stations";
    public const string Date = "sample.date";
    public const string DayCount = "sample.day-count";
    public const string Readings = "sample.readings";
    public const string Summary = "sample.reading-summary";

    public static Port One(string name, string contract) => new(name, contract, Version, Cardinality.One);
    public static Port Many(string name, string contract) => new(name, contract, Version, Cardinality.Many);
}

sealed record Station(string StationId, string Name, string Region, bool Offline);

static class StationCatalog
{
    // The South station "ST-05" is offline on purpose: it shows a failed item staying visible as unavailable.
    public static readonly IReadOnlyList<Station> All =
    [
        new("ST-01", "Alder Point", "North", false),
        new("ST-02", "Birch Hollow", "North", false),
        new("ST-03", "Cedar Ridge", "North", false),
        new("ST-04", "Dune Flats", "South", false),
        new("ST-05", "Elm Crossing", "South", true),
        new("ST-06", "Fern Bay", "South", false),
        new("ST-07", "Granite Pass", "West", false),
        new("ST-08", "Harbor Light", "West", false),
    ];

    public static readonly IReadOnlyList<DatasetColumn> Columns =
    [
        new("stationId", DatasetColumnType.String, false),
        new("name", DatasetColumnType.String, false),
        new("region", DatasetColumnType.String, false),
    ];

    public static JsonObject Row(Station s) => new() { ["stationId"] = s.StationId, ["name"] = s.Name, ["region"] = s.Region };

    public static JsonObject Dataset(string title, IEnumerable<Station> stations, string invocationId) => Datasets.Create(
        title, Columns, stations.Select(Row), new(ExecutionMode.Simulated, DataOrigin.Synthetic, invocationId));
}

sealed class ListStations : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("sample.stations.list", Contracts.Version, "List synthetic stations in a region",
        [Contracts.One("region", Contracts.Region)], [Contracts.One("stations", Contracts.Stations)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadData]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var region = invocation.Inputs["region"]?.GetValue<string>()
            ?? throw new CapabilityFailure("InvalidInput", "A region is required.");
        var stations = StationCatalog.All.Where(s => s.Region == region).ToList();
        if (stations.Count == 0) throw new CapabilityFailure("UnknownRegion", $"There are no synthetic stations in {region}.");
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["stations"] = StationCatalog.Dataset($"Stations in {region}", stations, invocation.InvocationId),
        });
    }
}

sealed class GetStation : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("sample.stations.get", Contracts.Version, "Get one synthetic station",
        [Contracts.One("stationId", Contracts.StationId)], [Contracts.One("stations", Contracts.Stations)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadData]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var id = invocation.Inputs["stationId"]?.GetValue<string>()
            ?? throw new CapabilityFailure("InvalidInput", "A station id is required.");
        var station = StationCatalog.All.FirstOrDefault(s => s.StationId == id)
            ?? throw new CapabilityFailure("UnknownStation", $"There is no synthetic station {id}.");
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["stations"] = StationCatalog.Dataset($"Station {id}", [station], invocation.InvocationId),
        });
    }
}

sealed class GenerateReadings : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("sample.readings.generate", Contracts.Version, "Generate synthetic daily temperature readings",
        [Contracts.One("station", Contracts.Station), Contracts.One("asOf", Contracts.Date), Contracts.One("days", Contracts.DayCount)],
        [Contracts.One("readings", Contracts.Readings)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public static readonly IReadOnlyList<DatasetColumn> Columns =
    [
        new("stationId", DatasetColumnType.String, false),
        new("date", DatasetColumnType.Date, false),
        new("temperatureC", DatasetColumnType.Number, false),
    ];

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var stationId = invocation.Inputs["station"]?["stationId"]?.GetValue<string>()
            ?? throw new CapabilityFailure("InvalidInput", "A station is required.");
        var station = StationCatalog.All.FirstOrDefault(s => s.StationId == stationId)
            ?? throw new CapabilityFailure("UnknownStation", $"There is no synthetic station {stationId}.");
        if (station.Offline) throw new CapabilityFailure("SourceUnavailable", $"Synthetic station {stationId} is offline.");
        var asOfText = invocation.Inputs["asOf"]?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", "asOf is required.");
        if (!DateOnly.TryParseExact(asOfText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
            throw new CapabilityFailure("InvalidInput", $"asOf '{asOfText}' is not a yyyy-MM-dd date.");
        var days = invocation.Inputs["days"]?.GetValue<int>() ?? throw new CapabilityFailure("InvalidInput", "days is required.");
        if (days is < 1 or > 31) throw new CapabilityFailure("InvalidInput", "days is between 1 and 31.");

        // Deterministic: the same station, date and length always give the same readings.
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{stationId}|{asOfText}|{days}")));
        var random = new Random(seed);
        var baseline = 8 + 2 * (stationId[^1] - '0');
        var rows = Enumerable.Range(0, days).Select(d =>
        {
            var date = asOf.AddDays(d - days + 1);
            var value = Math.Round(baseline + 4 * Math.Sin(date.DayOfYear / 58.0) + (random.NextDouble() - 0.5) * 3, 1);
            return new JsonObject { ["stationId"] = stationId, ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["temperatureC"] = value };
        });
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["readings"] = Datasets.Create($"Readings at {station.Name}", Columns, rows,
                new(ExecutionMode.Simulated, DataOrigin.Synthetic, invocation.InvocationId)),
        });
    }
}

sealed class SummariseReadings : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("sample.readings.summarise", Contracts.Version, "Summarise readings per station",
        [Contracts.Many("readings", Contracts.Readings)], [Contracts.One("summary", Contracts.Summary)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public static readonly IReadOnlyList<DatasetColumn> Columns =
    [
        new("stationId", DatasetColumnType.String, false),
        new("status", DatasetColumnType.String, false),
        new("count", DatasetColumnType.Integer, true),
        new("minC", DatasetColumnType.Number, true),
        new("maxC", DatasetColumnType.Number, true),
        new("meanC", DatasetColumnType.Number, true),
        new("readings", DatasetColumnType.DataRef, true),
        new("error", DatasetColumnType.String, true),
    ];

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var reference = invocation.Inputs["readings"].Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", "Readings are required.");
        var collection = invocation.Data.ReadRequired<OutcomeCollection>(reference);
        var rows = collection.Items.Select(item =>
        {
            var stationId = item.Identity.TryGetValue("stationId", out var id) ? id : item.ItemKey;
            if (item.Status != OutcomeStatus.Succeeded)
                // A station without readings is unavailable, never a row of zeroes.
                return new JsonObject
                {
                    ["stationId"] = stationId, ["status"] = "unavailable", ["count"] = null, ["minC"] = null, ["maxC"] = null,
                    ["meanC"] = null, ["readings"] = null, ["error"] = item.Error?.Message,
                };
            var readingsRef = item.Value.Deserialize<TypedDataRef>(WireJson.Options)!;
            var values = invocation.Data.ReadRequired<Dataset>(readingsRef).Rows.Select(r => r["temperatureC"]!.GetValue<double>()).ToList();
            return new JsonObject
            {
                ["stationId"] = stationId,
                ["status"] = "available",
                ["count"] = values.Count,
                ["minC"] = values.Min(),
                ["maxC"] = values.Max(),
                ["meanC"] = Math.Round(values.Average(), 2),
                ["readings"] = JsonSerializer.SerializeToNode(readingsRef, WireJson.Options),
                ["error"] = null,
            };
        });
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["summary"] = Datasets.Create("Station summary", Columns, rows, new(ExecutionMode.Simulated, DataOrigin.Synthetic, invocation.InvocationId)),
        });
    }
}
