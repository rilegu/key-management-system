var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Liveness only. It reports that the host is up, never that custody state is consistent —
// a probe that consults the database would fail the host during a transient lock.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("Health");

app.Run();

/// <summary>
/// Named so integration tests can reference the entry point through WebApplicationFactory.
/// </summary>
public partial class Program;
