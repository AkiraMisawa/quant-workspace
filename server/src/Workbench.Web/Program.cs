using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;
using Workbench.Samples.Synthetic;
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
builder.Services.AddSingleton<CompositionRegistry>();
builder.Services.AddSingleton<IDataStore, InMemoryDataStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<ActionService>();

var app = builder.Build();
// Resolve the registry now, so an invalid registered definition stops startup instead of a later request.
app.Services.GetRequiredService<CompositionRegistry>();

app.MapWorkbenchApi();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

/// <summary>Exposed for the in-process API tests.</summary>
public partial class Program;
