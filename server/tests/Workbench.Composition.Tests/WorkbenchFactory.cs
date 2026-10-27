using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Workbench.Composition.Tests;

/// <summary>The web host on a database of its own, removed afterwards.</summary>
public sealed class WorkbenchFactory : WebApplicationFactory<Program>
{
    readonly string database = Path.Combine(Path.GetTempPath(), $"workbench-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Workbench:Database", database).UseSetting("Workbench:DemoUser", "akira");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { database, database + "-wal", database + "-shm" }) File.Delete(file);
    }
}
