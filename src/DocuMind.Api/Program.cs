using DocuMind.Api.Endpoints;
using DocuMind.Core;
using DocuMind.Core.Storage;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDocuMindCore(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Uploads are bounded by Ingestion:MaxFileSizeBytes; allow a little multipart overhead on top.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = 12 * 1024 * 1024);

var app = builder.Build();

await app.Services.GetRequiredService<DocumentStore>().InitializeAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("DocuMind API"));

app.MapDocumentEndpoints();
app.MapSearchEndpoints();
app.MapAskEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" })).ExcludeFromDescription();

await app.RunAsync();

public partial class Program;
