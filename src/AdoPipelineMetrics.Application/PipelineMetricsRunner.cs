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
                    var outlierStatus = EvaluateOutlier(build, timeline.Stage);

                    var report = new BuildReport(
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
                        outlierStatus.IsOutlier,
                        outlierStatus.ExcludedFromMetrics,
                        outlierStatus.Reason,
                        timeline.Tasks,
                        artifacts);

                    if (!report.ExcludedFromMetrics || options.Value.OutlierFilter.KeepInRawJson)
                    {
                        buildReports.Add(report);
                    }
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

    private BuildOutlierStatus EvaluateOutlier(BuildRunInfo build, TimelineStageInfo? stage)
    {
        var filter = options.Value.OutlierFilter;
        if (stage is null)
        {
            const string reason = "Build stage was not found in timeline";
            logger.LogWarning("Build {BuildId} excluded from metrics. Reason={Reason}", build.Id, reason);
            return new BuildOutlierStatus(false, true, reason);
        }

        if (stage.StartTime is null || stage.FinishTime is null || stage.DurationSeconds is null)
        {
            const string reason = "Build stage does not have startTime and finishTime";
            logger.LogWarning("Build {BuildId} excluded from metrics. Reason={Reason}", build.Id, reason);
            return new BuildOutlierStatus(false, true, reason);
        }

        if (!filter.Enabled)
        {
            return new BuildOutlierStatus(false, false, null);
        }

        var durationMinutes = stage.DurationSeconds.Value / 60d;
        if (durationMinutes > filter.MaxBuildStageDurationMinutes)
        {
            var reason = $"Build stage duration exceeded {filter.MaxBuildStageDurationMinutes:0.####} minutes";
            logger.LogWarning(
                "Build {BuildId} excluded from metrics as outlier. BuildStageDurationMinutes={BuildStageDurationMinutes}, ThresholdMinutes={ThresholdMinutes}. Reason={Reason}",
                build.Id,
                durationMinutes,
                filter.MaxBuildStageDurationMinutes,
                filter.Reason);
            return new BuildOutlierStatus(true, true, reason);
        }

        return new BuildOutlierStatus(false, false, null);
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

        if (current.OutlierFilter.MaxBuildStageDurationMinutes <= 0)
        {
            throw new InvalidOperationException("AzureDevOps:OutlierFilter:MaxBuildStageDurationMinutes must be greater than zero.");
        }
    }

    private sealed record BuildOutlierStatus(bool IsOutlier, bool ExcludedFromMetrics, string? Reason);
}
