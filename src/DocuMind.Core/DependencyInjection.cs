using DocuMind.Core.Ingestion;
using DocuMind.Core.Retrieval;
using DocuMind.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocuMind.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddDocuMindCore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Documents")
            ?? throw new InvalidOperationException("Connection string 'Documents' is not configured.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SqliteConnectionFactory(connectionString));
        services.AddSingleton<DocumentStore>();

        services.AddSingleton(configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>() ?? new IngestionOptions());
        services.AddSingleton(new TextChunker());
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<IngestionService>();
        services.AddSingleton<SearchService>();

        return services;
    }
}
