using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>What reading a reference found. An expired or unknown reference is never recomputed silently.</summary>
public abstract record DataLookup
{
    public sealed record Found(JsonNode Content) : DataLookup;
    public sealed record Expired(TypedDataRef Reference) : DataLookup;
    public sealed record Unknown(TypedDataRef Reference) : DataLookup;
}

public interface IDataReader
{
    DataLookup Read(TypedDataRef reference);
}

/// <summary>Retained immutable values, addressed by reference. A revision is the digest of its content.</summary>
public interface IDataStore : IDataReader
{
    TypedDataRef Put(string dataId, string contractId, string contractVersion, JsonNode content);

    /// <summary>Ends a value's retention. Readers then see it as expired, not as missing.</summary>
    void Expire(TypedDataRef reference);
}

/// <summary>The slice's store: process memory only. It claims no durability (slice 01 plan).</summary>
public sealed class InMemoryDataStore : IDataStore
{
    readonly ConcurrentDictionary<(string DataId, string Revision), Entry> entries = new();

    sealed record Entry(TypedDataRef Reference, string Json, bool Expired);

    public TypedDataRef Put(string dataId, string contractId, string contractVersion, JsonNode content)
    {
        var (reference, json) = RetainedData.Prepare(dataId, contractId, contractVersion, content);
        var entry = entries.GetOrAdd((dataId, reference.Revision), _ => new Entry(reference, json, false));
        if (entry.Reference != reference)
            throw new InvalidOperationException($"Data {dataId}@{reference.Revision} is already retained under another contract.");
        return reference;
    }

    public DataLookup Read(TypedDataRef reference)
    {
        if (!entries.TryGetValue((reference.DataId, reference.Revision), out var entry) || entry.Reference != reference)
            return new DataLookup.Unknown(reference);
        if (entry.Expired) return new DataLookup.Expired(reference);
        return new DataLookup.Found(JsonNode.Parse(entry.Json) ?? throw new InvalidOperationException("Retained JSON is null."));
    }

    public void Expire(TypedDataRef reference)
    {
        if (!entries.TryGetValue((reference.DataId, reference.Revision), out var entry))
            throw new InvalidOperationException($"Cannot expire unknown data {reference.DataId}@{reference.Revision}.");
        entries[(reference.DataId, reference.Revision)] = entry with { Expired = true };
    }
}

/// <summary>How every store addresses a value: its revision is the digest of its content.</summary>
public static class RetainedData
{
    /// <summary>The reference a value is retained under, and the JSON to retain.</summary>
    public static (TypedDataRef Reference, string Json) Prepare(string dataId, string contractId, string contractVersion, JsonNode content)
    {
        // A dataset document names its own reference; the revision is the digest of everything else.
        var body = content.DeepClone();
        var isDataset = body is JsonObject { } obj && obj["documentType"]?.GetValue<string>() == "dataset";
        if (isDataset) ((JsonObject)body).Remove("reference");
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.ToJsonString(WireJson.Options))))[..16];
        var reference = new TypedDataRef(dataId, contractId, contractVersion, revision);
        if (isDataset) ((JsonObject)body)["reference"] = JsonSerializer.SerializeToNode(reference, WireJson.Options);
        return (reference, body.ToJsonString(WireJson.Options));
    }
}

public static class DataReaderExtensions
{
    /// <summary>Reads a reference that must be available, or fails the invocation with a typed code.</summary>
    public static JsonNode ReadRequired(this IDataReader reader, TypedDataRef reference) => reader.Read(reference) switch
    {
        DataLookup.Found found => found.Content,
        DataLookup.Expired => throw new CapabilityFailure("DataExpired", $"{reference.DataId}@{reference.Revision} has expired."),
        _ => throw new CapabilityFailure("DataUnavailable", $"{reference.DataId}@{reference.Revision} is not retained."),
    };

    public static T ReadRequired<T>(this IDataReader reader, TypedDataRef reference) =>
        reader.ReadRequired(reference).Deserialize<T>(WireJson.Options)
        ?? throw new CapabilityFailure("DataUnavailable", $"{reference.DataId}@{reference.Revision} is null.");
}
