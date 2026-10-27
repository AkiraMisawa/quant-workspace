namespace Workbench.AgentHost;

// The agent host (ADR-0007 §5, C44, C45). Started beside the workbench on the same machine, under the agent's own
// identity: COPILOT_GITHUB_TOKEN is set for this process and never for the workbench. An explicit entry point, so its
// Program is not the global one the workbench's tests address.
static class AgentHostProgram
{
    static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var url = builder.Configuration["AgentHost:Url"] ?? throw new InvalidOperationException("AgentHost:Url is not set.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException($"AgentHost:Url is {url}; the agent host listens on a loopback address only.");
        builder.WebHost.UseUrls(url);
        CopilotBackendOptions.LoadToken(builder.Configuration);
        builder.Services.AddSingleton(CopilotBackendOptions.From(builder.Configuration, builder.Environment));
        builder.Services.AddSingleton<IModelBackend, CopilotBackend>();
        await using var app = AgentHostApp.Build(builder);
        await app.RunAsync();
    }
}
