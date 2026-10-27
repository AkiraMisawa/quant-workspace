using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Persistence.Sqlite;
using Workbench.Samples.Synthetic;
using Workbench.Samples.Xva;
using Workbench.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var converter in WireJson.Options.Converters) o.SerializerOptions.Converters.Add(converter);
    o.SerializerOptions.UnmappedMemberHandling = WireJson.Options.UnmappedMemberHandling;
    o.SerializerOptions.DefaultIgnoreCondition = WireJson.Options.DefaultIgnoreCondition;
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});

// The composition root: the only place that names modules and providers.
builder.Services.AddSingleton<IWorkbenchModule, SyntheticModule>();
builder.Services.AddSingleton<IWorkbenchModule, XvaModule>();
builder.Services.AddSingleton<CompositionRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<ActionService>();

// Workspaces: one SQLite file (D04, C35). Each module registers its edit rules and how its sources are re-acquired.
var database = Path.GetFullPath(builder.Configuration["Workbench:Database"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "workbench.db"));
Directory.CreateDirectory(Path.GetDirectoryName(database)!);
builder.Services.AddSingleton(_ => new SqliteRunStore(database));
builder.Services.AddSingleton<IRunStore>(sp => sp.GetRequiredService<SqliteRunStore>());
builder.Services.AddSingleton<IDataStore>(sp => sp.GetRequiredService<SqliteRunStore>());
builder.Services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(database,
    new Dictionary<string, SourceResolver> { [XvaEditRules.App] = XvaSourceVersion.Resolve }));
builder.Services.AddSingleton(sp => new WorkspaceService(
    new Dictionary<string, IEditRules> { [XvaEditRules.App] = new XvaEditRules() },
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IWorkspaceStore>()));
builder.Services.AddSingleton<XvaWorkspaces>();
builder.Services.AddSingleton(new CurrentUser(builder.Configuration["Workbench:DemoUser"] ?? "demo"));

var app = builder.Build();
// Resolve the registry now, so an invalid registered definition stops startup instead of a later request.
app.Services.GetRequiredService<CompositionRegistry>();

app.MapWorkbenchApi();
app.MapXvaApi();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

/// <summary>Exposed for the in-process API tests.</summary>
public partial class Program;
