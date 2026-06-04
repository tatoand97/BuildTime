using AdoPipelineMetrics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdoPipelineMetrics.Application;

public sealed class PipelineMetricsRunner(
    IRepositoryService repositoryService,
    IPipelineDefinitionService definitionService,
    IBuildRunService buildRunService,
    ITimelineService timelineService,
    IArtifactService artifactService,
    IMetricsCalculator metricsCalculator,
    ICsvExporter csvExporter,
    IJsonExporter jsonExporter,
    IOptions<AzureDevOpsOptions> options,
    ILogger<PipelineMetricsRunner> logger) : IPipelineMetricsRunner
{
    public async Task<ExtractionResult> RunAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();

        var repositories = new List<RepositoryReport>();
        foreach (var repository in await repositoryService.GetConfiguredRepositoriesAsync(cancellationToken))
        {
            var pipelineReports = new List<PipelineReport>();
            foreach (var definition in await definitionService.GetDefinitionsAsync(repository, cancellationToken))
            {
                var buildReports = new List<BuildReport>();
                foreach (var build in await buildRunService.GetBuildsAsync(definition, repository, cancellationToken))
                {
                    var timeline = await timelineService.GetBuildStageAsync(build.Id, cancellationToken);
                    var artifacts = await artifactService.GetArtifactsAsync(build.Id, cancellationToken);

                    buildReports.Add(new BuildReport(
                        build.Id,
                        build.BuildNumber,
                        build.SourceBranch,
                        build.Status,
                        build.Result,
                        build.QueueTime,
                        build.StartTime,
                        build.FinishTime,
                        build.QueueDurationSeconds,
                        build.BuildDurationSeconds,
                        build.QueueToFinishDurationSeconds,
                        timeline.Stage?.DurationSeconds,
                        timeline.Tasks,
                        artifacts));
                }

                pipelineReports.Add(new PipelineReport(definition.Id, definition.Name, buildReports));
            }

            repositories.Add(new RepositoryReport(repository.Name, repository.Id, pipelineReports));
        }

        var result = new ExtractionResult(
            DateTimeOffset.UtcNow,
            repositories,
            metricsCalculator.CalculateBuildSummaries(repositories),
            metricsCalculator.CalculateTaskSummaries(repositories));

        Directory.CreateDirectory(options.Value.OutputPath);
        await jsonExporter.ExportAsync(result, options.Value.OutputPath, cancellationToken);
        await csvExporter.ExportAsync(result, options.Value.OutputPath, cancellationToken);

        logger.LogInformation("Extraction finished. Repositories={RepositoryCount}, OutputPath={OutputPath}", repositories.Count, options.Value.OutputPath);
        return result;
    }

    private void ValidateOptions()
    {
        var current = options.Value;
        if (string.IsNullOrWhiteSpace(current.Organization))
        {
            throw new InvalidOperationException("AzureDevOps:Organization is required.");
        }

        if (string.IsNullOrWhiteSpace(current.Project))
        {
            throw new InvalidOperationException("AzureDevOps:Project is required.");
        }

        if (current.TopBuilds <= 0)
        {
            throw new InvalidOperationException("AzureDevOps:TopBuilds must be greater than zero.");
        }
    }
}
