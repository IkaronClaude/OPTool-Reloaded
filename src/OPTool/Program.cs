using OPTool;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionManager>());

var app = builder.Build();

app.MapGet("/health", () => "ok");

app.MapGet("/status", (ConnectionManager cm) => cm.Status);

app.Run();
