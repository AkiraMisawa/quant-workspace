using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Products;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Composition.Tests;

// ADR-0008: the kernel reads typed words against a product's description and creates one entity. The product here is
// non-financial on purpose (as CO01): a station reading, so nothing in the kernel can lean on rates.
public class ProductTests
{
    static readonly ProductDescriptor Reading = new("weather.reading", "1.0.0", "weather", "readings", "reading", "Station reading",
    [
        new("station", "Station", FieldForm.Choice, Required: true, Unnamed: true, Choices: new Dictionary<string, string> { ["north"] = "NORTH", ["south"] = "SOUTH" }),
        new("window", "Window", FieldForm.Period, Required: true, Unnamed: true),
        new("volume", "Volume", FieldForm.Amount, Unnamed: true),
        new("delta", "Change", FieldForm.Percent, Unnamed: true),
        new("from", "First", FieldForm.Choice, Unnamed: true, Sequence: "pair", Choices: new Dictionary<string, string> { ["a"] = "A", ["b"] = "B" }),
        new("to", "Second", FieldForm.Choice, Unnamed: true, Sequence: "pair", Choices: new Dictionary<string, string> { ["a"] = "A", ["b"] = "B" }),
        new("lag", "Lag", FieldForm.Period, Aliases: ["l"]),
        new("since", "Since", FieldForm.Date),
    ]);

    [Fact] // ADR-0008 §3: words in any order, each read by its form; a named term by its name or alias
    public void Words_are_read_by_their_form_in_any_order()
    {
        var (values, issues) = ProductReader.Read(Reading, "3m South 250 l=2d 1.5%");

        Assert.Empty(issues);
        Assert.Equal("SOUTH", values["station"]!.GetValue<string>());
        Assert.Equal("3m", values["window"]!.GetValue<string>());
        Assert.Equal(250e6, values["volume"]!.GetValue<double>());
        Assert.Equal(1.5, values["delta"]!.GetValue<double>());
        Assert.Equal("2d", values["lag"]!.GetValue<string>());
    }

    [Fact] // ADR-0008 §2, §3: fields of one sequence take words of their form in the order declared
    public void A_sequence_fills_in_order()
    {
        var (values, issues) = ProductReader.Read(Reading, "b a");
        Assert.Empty(issues);
        Assert.Equal(("B", "A"), (values["from"]!.GetValue<string>(), values["to"]!.GetValue<string>()));
    }

    [Fact] // ADR-0008 §3: a word that fits two unnamed fields of no sequence is refused, never guessed
    public void An_ambiguous_word_is_refused()
    {
        var ambiguous = Reading with { Fields = [.. Reading.Fields, new ProductField("span", "Span", FieldForm.Period, Unnamed: true)] };
        var (values, issues) = ProductReader.Read(ambiguous, "6m");

        Assert.Empty(values);
        Assert.Equal("AmbiguousWord", Assert.Single(issues).Code);
        Assert.Contains("window or span", issues[0].Message);
    }

    [Fact] // ADR-0008 §3: a word no field takes, a term named twice and an unknown name are each refused by name
    public void Unread_repeated_and_unknown_terms_are_refused()
    {
        var (_, issues) = ProductReader.Read(Reading, "north north window=3m window=6m colour=red");
        Assert.Equal(["UnreadWord", "RepeatedTerm", "UnknownTerm"], issues.Select(i => i.Code));
    }

    [Fact] // ADR-0008 §2: m is months, so an amount in millions is bare or mm; the two forms never share a word
    public void A_period_and_an_amount_never_share_a_word()
    {
        var (values, issues) = ProductReader.Read(Reading, "north 6m 500mm");
        Assert.Empty(issues);
        Assert.Equal(("6m", 500e6), (values["window"]!.GetValue<string>(), values["volume"]!.GetValue<double>()));
    }

    [Fact] // ADR-0008 §2 (owner, 2026-11-04): a bare amount is millions; a bare million or more is refused, not made a trillion
    public void A_bare_amount_is_millions_and_a_full_amount_is_refused()
    {
        Assert.Equal(2.5e6, ProductReader.Read(Reading, "volume=2.5").Values["volume"]!.GetValue<double>());
        Assert.Equal(3e9, ProductReader.Read(Reading, "volume=3bn").Values["volume"]!.GetValue<double>());
        Assert.Equal(500e6, ProductReader.Read(Reading, "volume=500mm").Values["volume"]!.GetValue<double>());
        Assert.Equal(0.05, ProductReader.Read(Reading, "delta=5bp").Values["delta"]!.GetValue<double>());

        var (values, issues) = ProductReader.Read(Reading, "1,000,000,000");
        Assert.False(values.ContainsKey("volume"));
        var issue = Assert.Single(issues);
        Assert.Equal("volume", issue.Field);
        Assert.Contains("millions", issue.Message);
    }

    sealed class Rules(WorkspaceService workspaces) : IProductRules
    {
        public string ProductId => Reading.Id;

        public IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given) =>
            workspace.Context["window"] is { } window ? new Dictionary<string, ProductDefault> { ["window"] = new(window.DeepClone(), "workspace") } : [];

        public (WorkspaceState State, string Entity) Create(string workspaceId, long? revision, string user, IReadOnlyDictionary<string, JsonNode?> terms,
            IReadOnlyDictionary<string, string> sources, RunOrigin origin)
        {
            var state = workspaces.Find(workspaceId)!;
            var entity = $"R{state.Revision + 1}";
            var fields = new JsonObject(terms.Select(t => KeyValuePair.Create(t.Key, t.Value?.DeepClone())));
            return (workspaces.Create(workspaceId, revision ?? state.Revision, user, Reading.Scope, entity, fields, origin, sources), entity);
        }
    }

    sealed class Catalog : IProductCatalog { public IEnumerable<ProductDescriptor> Products => [Reading]; }

    sealed class NoRules : IEditRules
    {
        public string KindOf(EditTarget target) => "weather.reading";
        public IReadOnlyList<string> ConflictGroup(EditTarget target) => [target.Field];
        public IReadOnlyDictionary<string, ISourceVersion> Derive(WorkspaceState state) => new Dictionary<string, ISourceVersion>();
    }

    sealed class NoSource : ISourceVersion
    {
        public string Version => "none";
        public JsonObject Descriptor => [];
        public SourceValue? Read(string entity, string field) => null;
    }

    static (ProductService Products, WorkspaceService Workspaces, string Id) Workbench(JsonObject context)
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { ["weather"] = new NoRules() }, TimeProvider.System);
        var id = workspaces.Create("ana", "weather", context, new NoSource()).WorkspaceId;
        return (new ProductService([new Catalog()], [new Rules(workspaces)], workspaces), workspaces, id);
    }

    [Fact] // ADR-0008 §1, §4, CO01: a product added without a kernel change is created, its defaulted fields recorded with their source
    public void A_described_product_is_created_with_its_defaults_recorded()
    {
        var (products, workspaces, id) = Workbench(new JsonObject { ["window"] = "1d" });

        var (state, entity, reading) = products.Create(id, null, "ana", Reading.Id, "north 10", RunOrigin.Pricer);

        var created = state.Created[entity];
        Assert.Equal("1d", created.Fields["window"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, string> { ["window"] = "workspace" }, created.Sources);
        Assert.Equal("defaults: window from workspace", workspaces.Find(id)!.Log[^1].Note);
        Assert.Equal(reading.Sources, created.Sources);
    }

    [Fact] // ADR-0008 §1: every missing term is refused by name, with every unread word, and nothing is written
    public void Missing_and_unread_terms_refuse_the_whole_command()
    {
        var (products, workspaces, id) = Workbench([]);
        var before = workspaces.Find(id)!.Revision;

        var refused = Assert.Throws<ProductTermsRefused>(() => products.Create(id, null, "ana", Reading.Id, "purple", RunOrigin.Pricer));

        Assert.Equal(["UnreadWord", "MissingTerm", "MissingTerm"], refused.Issues.Select(i => i.Code));
        Assert.Equal(["station", "window"], refused.Issues.Where(i => i.Code == "MissingTerm").Select(i => i.Field!));
        Assert.Equal(before, workspaces.Find(id)!.Revision);
    }

    [Fact] // ADR-0008 §7: a product is created only in a workspace of its app, by its owner
    public void A_product_is_refused_outside_its_app_or_owner()
    {
        var (products, _, id) = Workbench([]);
        Assert.Equal("NotOwner", Assert.Throws<WorkspaceRefused>(() => products.Preview(id, "bo", Reading.Id, "north 1d")).Code);
        Assert.Equal("UnknownProduct", Assert.Throws<WorkspaceRefused>(() => products.Preview(id, "ana", "weather.forecast", "")).Code);
    }
}
