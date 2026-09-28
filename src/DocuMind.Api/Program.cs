var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "DocuMind API");

await app.RunAsync();

public partial class Program;
