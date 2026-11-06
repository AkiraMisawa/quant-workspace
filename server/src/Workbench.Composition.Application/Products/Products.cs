using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Composition.Application.Products;

// ADR-0008: one generic create command, and products that are descriptions. The kernel reads what was typed against a
// product's description and asks the product's module only for what a description cannot say: its defaults, its
// mapping onto the entity, and whether the terms make economic sense. Nothing here knows a currency, a leg or a rate.

/// <summary>How a word is read (ADR-0008 §2). A form says how a word is written, never what it means.</summary>
public enum FieldForm
{
    /// <summary>A period such as <c>3m</c> or <c>10y</c>, kept lower case.</summary>
    Period,
    /// <summary>A date, <c>yyyy-MM-dd</c>.</summary>
    Date,
    /// <summary>A period or a date.</summary>
    PeriodOrDate,
    /// <summary>
    /// An amount: <c>3bn</c>, <c>500mm</c>, <c>20k</c>, or a bare number in millions (owner, 2026-11-04). A bare number of a
    /// million or more is refused: it is almost surely a full amount typed out of habit, a million times too large.
    /// </summary>
    Amount,
    /// <summary>A rate in percent: <c>1.25%</c>, or in basis points: <c>5bp</c> (0.05). A bare number is not a rate.</summary>
    Percent,
    /// <summary>One of the field's choices, by any of its words, in any case.</summary>
    Choice,
    /// <summary><c>true</c> or <c>false</c>, only by name.</summary>
    Flag,
    /// <summary>A plain number, only by name.</summary>
    Number,
}

/// <param name="Name">The field's name in a reading, and what <c>name=value</c> takes.</param>
/// <param name="Aliases">Other names <c>name=value</c> takes (<c>fixed</c> for a fixed leg's frequency).</param>
/// <param name="Unnamed">Whether a word may fill it without its name, by its form alone (ADR-0008 §3).</param>
/// <param name="Sequence">
/// Fields of one sequence take unnamed words of their form in the order declared (a cross-currency swap's two
/// currencies). Unnamed fields that share a form and no sequence are ambiguous, and such a word is refused.
/// </param>
/// <param name="Choices">For <see cref="FieldForm.Choice"/>: each word the user may type, lower case, and the value it reads as.</param>
public sealed record ProductField(
    string Name,
    string Title,
    FieldForm Form,
    bool Required = false,
    bool Unnamed = false,
    string? Sequence = null,
    IReadOnlyDictionary<string, string>? Choices = null,
    IReadOnlyList<string>? Aliases = null);

/// <summary>
/// A product the workbench can create (ADR-0008 §7): data, from a catalog. <see cref="App"/> and <see cref="Scope"/>
/// say where its entities live; <see cref="Words"/> is what follows <c>new</c> on the command line.
/// </summary>
public sealed record ProductDescriptor(
    string Id,
    string Version,
    string App,
    string Scope,
    string Words,
    string Title,
    IReadOnlyList<ProductField> Fields);

/// <summary>
/// Where descriptors come from (ADR-0008 §7). A module's own catalog in this repository; an adapter over the analytics
/// engine's product catalog at the company, with no kernel or UI change.
/// </summary>
public interface IProductCatalog
{
    IEnumerable<ProductDescriptor> Products { get; }
}

/// <summary>A value the user did not type, and where it came from (ADR-0008 §4): <c>profile:…</c>, <c>workspace</c>, <c>C47</c>.</summary>
public sealed record ProductDefault(JsonNode? Value, string Source);

/// <summary>
/// What a description cannot say, from the product's module (ADR-0008 §7): the defaults it may supply, and creating
/// the entity, which maps the terms onto its fields and checks that they make economic sense.
/// </summary>
public interface IProductRules
{
    string ProductId { get; }

    /// <summary>
    /// The values this product supplies for fields the user left out, given what was typed and the workspace. Only
    /// from a confirmed template, the workspace's own defaults or a confirmed decision; never from another entity,
    /// the last input or a market feed (ADR-0008 §4).
    /// </summary>
    IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given);

    /// <summary>
    /// Creates the entity from complete terms at the revision named (the current one when null), or refuses with every
    /// reason, writing nothing (<see cref="ProductTermsRefused"/>).
    /// </summary>
    (WorkspaceState State, string Entity) Create(string workspaceId, long? revision, string user, IReadOnlyDictionary<string, JsonNode?> terms,
        IReadOnlyDictionary<string, string> sources, RunOrigin origin);
}

/// <summary>Terms that do not make the product: every reason, by field where there is one. Nothing was written.</summary>
public sealed class ProductTermsRefused(IReadOnlyList<ProductIssue> issues) : Exception(string.Join(" ", issues.Select(i => i.Message)))
{
    public IReadOnlyList<ProductIssue> Issues { get; } = issues;
}

/// <param name="Field">The field it is about, or null for a word that fills none or the terms as a whole.</param>
public sealed record ProductIssue(string Code, string? Field, string Message);

/// <summary>What was typed, read against a description: each field's value, which came from a default and from where, and every issue.</summary>
public sealed record ProductReading(
    IReadOnlyDictionary<string, JsonNode?> Values,
    IReadOnlyDictionary<string, string> Sources,
    IReadOnlyList<string> Missing,
    IReadOnlyList<ProductIssue> Issues)
{
    public bool Complete => Missing.Count == 0 && Issues.Count == 0;
}

/// <summary>Reads words against a description (ADR-0008 §3). Pure: the same words and description read the same.</summary>
public static partial class ProductReader
{
    /// <summary>Splits the typed terms into words, and reads each by name or by form.</summary>
    public static (Dictionary<string, JsonNode?> Values, List<ProductIssue> Issues) Read(ProductDescriptor product, string text)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var issues = new List<ProductIssue>();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = word.IndexOf('=');
            if (eq > 0)
            {
                var name = word[..eq];
                var field = product.Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (f.Aliases?.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? false));
                if (field is null)
                {
                    issues.Add(new("UnknownTerm", null, $"{product.Words} has no term named {name}; it takes {string.Join(", ", product.Fields.Select(NameOf))}."));
                    continue;
                }
                Fill(field, word[(eq + 1)..]);
                continue;
            }
            var candidates = product.Fields.Where(f => f.Unnamed && !values.ContainsKey(f.Name) && Parse(f, word).Ok).ToList();
            if (candidates.Count == 0)
            {
                var amountMistake = product.Fields.FirstOrDefault(f => f.Unnamed && f.Form == FieldForm.Amount && !values.ContainsKey(f.Name) && IsFullAmount(word));
                issues.Add(amountMistake is not null
                    ? new("InvalidTerm", amountMistake.Name, Parse(amountMistake, word).Message!)
                    : new("UnreadWord", null, $"'{word}' is no term of {product.Words} left to fill; name it as name=value."));
                continue;
            }
            var sequence = candidates[0].Sequence;
            if (candidates.Count > 1 && (sequence is null || candidates.Any(c => c.Sequence != sequence)))
            {
                issues.Add(new("AmbiguousWord", null, $"'{word}' could be {string.Join(" or ", candidates.Select(c => c.Name))}; write it as name=value."));
                continue;
            }
            Fill(candidates[0], word);
        }
        return (values, issues);

        void Fill(ProductField field, string text)
        {
            if (values.ContainsKey(field.Name))
            {
                issues.Add(new("RepeatedTerm", field.Name, $"{field.Name} is given twice."));
                return;
            }
            var parsed = Parse(field, text);
            if (parsed.Ok) values[field.Name] = parsed.Value;
            else issues.Add(new("InvalidTerm", field.Name, parsed.Message!));
        }
    }

    /// <summary>
    /// Reads the words, fills what the product's defaults supply, and lists every required field still empty by name.
    /// </summary>
    public static ProductReading Complete(ProductDescriptor product, string text, Func<IReadOnlyDictionary<string, JsonNode?>, IReadOnlyDictionary<string, ProductDefault>> defaults)
    {
        var (values, issues) = Read(product, text);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, supplied) in defaults(values))
        {
            if (values.ContainsKey(name)) continue;
            if (product.Fields.All(f => f.Name != name))
                throw new InvalidOperationException($"The rules of {product.Id} default {name}, which its description does not have.");
            values[name] = supplied.Value?.DeepClone();
            sources[name] = supplied.Source;
        }
        var missing = product.Fields.Where(f => f.Required && (!values.TryGetValue(f.Name, out var v) || v is null)).Select(f => f.Name).ToList();
        return new(values, sources, missing, issues);
    }

    static string NameOf(ProductField f) => f.Name;

    public readonly record struct Parsed(bool Ok, JsonNode? Value, string? Message);

    static Parsed Fail(string message) => new(false, null, message);
    static Parsed Take(JsonNode? value) => new(true, value, null);

    public static Parsed Parse(ProductField field, string text)
    {
        var t = text.Trim();
        switch (field.Form)
        {
            case FieldForm.Period:
                return PeriodPattern().IsMatch(t) ? Take(t.ToLowerInvariant()) : Fail($"{field.Name} is a period such as 3m or 10y.");
            case FieldForm.Date:
                return IsDate(t) ? Take(t) : Fail($"{field.Name} is a date, yyyy-mm-dd.");
            case FieldForm.PeriodOrDate:
                return PeriodPattern().IsMatch(t) ? Take(t.ToLowerInvariant()) : IsDate(t) ? Take(t) : Fail($"{field.Name} is a period such as 10y, or a date.");
            case FieldForm.Amount:
            {
                var m = AmountPattern().Match(t);
                if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
                    return Fail($"{field.Name} is an amount in millions (1000 is a billion), or with its scale: 3bn, 500mm.");
                var scale = m.Groups[2].Value.ToLowerInvariant();
                if (scale.Length == 0 && n >= 1e6)
                    return Fail($"{field.Name} is in millions, so {t} would be {n:#,0} million; write it with its scale, such as 1bn.");
                return Take(n * scale switch { "k" => 1e3, "mm" => 1e6, "bn" => 1e9, _ => 1e6 });
            }
            case FieldForm.Percent:
            {
                var m = PercentPattern().Match(t);
                if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return Fail($"{field.Name} is a rate with its unit: 1.25% or 5bp.");
                return Take(m.Groups[2].Value.Equals("bp", StringComparison.OrdinalIgnoreCase) ? n / 100 : n);
            }
            case FieldForm.Choice:
            {
                var choices = field.Choices ?? throw new InvalidOperationException($"{field.Name} is a choice with no choices.");
                return choices.TryGetValue(t.ToLowerInvariant(), out var v) ? Take(v) : Fail($"{field.Name} is one of {string.Join(", ", choices.Keys)}.");
            }
            case FieldForm.Flag:
                return bool.TryParse(t, out var b) ? Take(b) : Fail($"{field.Name} is true or false.");
            default:
                return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Take(d) : Fail($"{field.Name} is a number.");
        }
    }

    /// <summary>A bare number of a million or more: a full amount typed where millions are read (owner, 2026-11-04).</summary>
    static bool IsFullAmount(string t)
    {
        var m = AmountPattern().Match(t.Trim());
        return m.Success && m.Groups[2].Value.Length == 0
            && double.TryParse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 1e6;
    }

    static bool IsDate(string t) => DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex("^[1-9][0-9]{0,2}[dwmy]$", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodPattern();

    [GeneratedRegex("^([0-9][0-9,]*(?:\\.[0-9]+)?)(k|mm|bn)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex("^(-?[0-9]+(?:\\.[0-9]+)?)(%|bp)$", RegexOptions.IgnoreCase)]
    private static partial Regex PercentPattern();
}

/// <summary>
/// The products the workbench can create, and creating one (ADR-0008 §1): read what was typed, fill the defaults,
/// refuse every missing or unreadable term by name, then let the product's rules write one <c>Create</c> edit.
/// </summary>
public sealed class ProductService
{
    readonly Dictionary<string, ProductDescriptor> products;
    readonly Dictionary<string, IProductRules> rules;
    readonly WorkspaceService workspaces;

    public ProductService(IEnumerable<IProductCatalog> catalogs, IEnumerable<IProductRules> rules, WorkspaceService workspaces)
    {
        products = [];
        foreach (var p in catalogs.SelectMany(c => c.Products))
        {
            if (!products.TryAdd(p.Id, p)) throw new InvalidOperationException($"Product {p.Id} is described twice.");
            if (p.Fields.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Fields.Count)
                throw new InvalidOperationException($"Product {p.Id} names a field twice.");
            if (p.Fields.Any(f => f.Form == FieldForm.Choice && f.Choices is not { Count: > 0 }))
                throw new InvalidOperationException($"Product {p.Id} has a choice with nothing to choose.");
        }
        if (products.Values.GroupBy(p => (p.App, p.Words)).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"Products {string.Join(" and ", twice.Select(p => p.Id))} are both '{twice.Key.Words}'.");
        this.rules = rules.ToDictionary(r => r.ProductId);
        foreach (var id in this.rules.Keys.Where(id => !products.ContainsKey(id)))
            throw new InvalidOperationException($"Rules are registered for {id}, which no catalog describes.");
        this.workspaces = workspaces;
    }

    /// <summary>The products of an app that can be created here: described, with rules to create them.</summary>
    public IEnumerable<ProductDescriptor> Creatable(string? app = null) =>
        products.Values.Where(p => rules.ContainsKey(p.Id) && (app is null || p.App == app)).OrderBy(p => p.Id, StringComparer.Ordinal);

    /// <summary>What the words would make in this workspace, without creating it: for the command line to show.</summary>
    public ProductReading Preview(string workspaceId, string user, string productId, string text)
    {
        var (product, rule, state) = Resolve(workspaceId, user, productId);
        return ProductReader.Complete(product, text, given => rule.Defaults(state, given));
    }

    /// <summary>Creates one entity of the product from what was typed, at the revision the caller saw (the current one when null).</summary>
    public (WorkspaceState State, string Entity, ProductReading Reading) Create(string workspaceId, long? revision, string user, string productId, string text, RunOrigin origin)
    {
        var (product, rule, state) = Resolve(workspaceId, user, productId);
        var reading = ProductReader.Complete(product, text, given => rule.Defaults(state, given));
        if (!reading.Complete)
            throw new ProductTermsRefused([.. reading.Issues, .. reading.Missing.Select(m => new ProductIssue("MissingTerm", m,
                $"{m} is missing: type it, or set a default for it."))]);
        var (created, entity) = rule.Create(workspaceId, revision, user, reading.Values, reading.Sources, origin);
        return (created, entity, reading);
    }

    (ProductDescriptor, IProductRules, WorkspaceState) Resolve(string workspaceId, string user, string productId)
    {
        if (!products.TryGetValue(productId, out var product)) throw new WorkspaceRefused("UnknownProduct", $"No product {productId} is described.");
        if (!rules.TryGetValue(productId, out var rule)) throw new WorkspaceRefused("NotCreatable", $"{product.Title} is described, but nothing here can create it yet.");
        var state = workspaces.Find(workspaceId);
        if (state is null || state.App != product.App) throw new WorkspaceRefused("UnknownWorkspace", $"No {product.App} workspace {workspaceId}.");
        if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "This workspace belongs to another user.");
        return (product, rule, state);
    }
}
