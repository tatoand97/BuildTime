using AdoPipelineMetrics.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdoPipelineMetrics.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAdoPipelineMetrics(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));

        services.AddSingleton<IRepositoryService, RepositoryService>();
        services.AddSingleton<IPipelineDefinitionService, PipelineDefinitionService>();
        services.AddSingleton<IBuildRunService, BuildRunService>();
        services.AddSingleton<ITimelineService, TimelineService>();
        services.AddSingleton<IArtifactService, ArtifactService>();
        services.AddSingleton<IMetricsCalculator, MetricsCalculator>();
        services.AddSingleton<ICsvExporter, CsvExporter>();
        services.AddSingleton<IJsonExporter, JsonExporter>();
        services.AddSingleton<IPipelineMetricsRunner, PipelineMetricsRunner>();

        services.AddHttpClient<IAzureDevOpsClient, AzureDevOpsClient>();

        return services;
    }
}
