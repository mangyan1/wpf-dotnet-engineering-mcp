using EngineeringMcp.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEngineeringMcpBackendDiagnostics();
var app = builder.Build();
app.UseEngineeringMcpBackendDiagnostics();
app.MapGet("/ok", () => Results.Ok(new { status = "ok" }));
app.MapGet("/bad-request", () => Results.BadRequest(new { error = "fixture validation" }));
app.MapGet("/fail", () => Task.FromException<IResult>(new InvalidOperationException("Synthetic backend fixture failure; authorization=Bearer fake-token-value")));
app.MapGet("/slow", async () => { await Task.Delay(750); return Results.Ok(); });
app.Run();
