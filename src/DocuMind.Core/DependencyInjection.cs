using Anthropic;
using DocuMind.Core.Answering;
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

        AddAnswering(services, configuration);

        return services;
    }

    private static void AddAnswering(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ClaudeOptions.SectionName).Get<ClaudeOptions>() ?? new ClaudeOptions();
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey)
            ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            : options.ApiKey;

        services.AddSingleton(options);
        services.AddSingleton<AnswerService>();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Upload and search keep working without a key; /api/ask explains what is missing.
            services.AddSingleton<IAnswerGenerator, UnconfiguredAnswerGenerator>();
            return;
        }

        services.AddSingleton(new AnthropicClient { ApiKey = apiKey });
        services.AddSingleton<IAnswerGenerator, ClaudeAnswerGenerator>();
    }
}
