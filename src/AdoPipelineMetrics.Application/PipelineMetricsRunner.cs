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
                    var artifactReadyTime = GetArtifactReadyTime(timeline.Stage, timeline.Tasks);
                    var queueToArtifactReadySeconds = DurationSeconds(build.QueueTime, artifactReadyTime);
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
                        timeline.Stage?.StartTime,
                        timeline.Stage?.FinishTime,
                        artifactReadyTime,
                        timeline.Stage?.DurationSeconds,
                        queueToArtifactReadySeconds,
                        outlierStatus.IsOutlier,
                        outlierStatus.IsQueueOutlier,
                        outlierStatus.ExcludedFromMetrics,
                        outlierStatus.Reason,
                        timeline.Tasks,
                        artifacts);

                    if (!report.IsOutlier || !report.ExcludedFromMetrics || options.Value.OutlierFilter.KeepInRawJson)
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
        var policy = options.Value.MetricsInclusionPolicy;
        var reasons = new List<string>();
        var isOutlier = false;
        var isQueueOutlier = false;
        var excludedFromMetrics = false;

        if (stage is null)
        {
            const string reason = "Build stage was not found in timeline";
            logger.LogWarning("Build {BuildId} excluded from metrics. Reason={Reason}", build.Id, reason);
            if (policy.ExcludeMissingStageFromMetrics)
            {
                excludedFromMetrics = true;
            }

            reasons.Add(reason);
        }
        else if (stage.StartTime is null || stage.FinishTime is null || stage.DurationSeconds is null)
        {
            const string reason = "Build stage does not have startTime and finishTime";
            logger.LogWarning("Build {BuildId} excluded from metrics. Reason={Reason}", build.Id, reason);
            if (policy.ExcludeMissingStageFromMetrics)
            {
                excludedFromMetrics = true;
            }

            reasons.Add(reason);
        }

        if (policy.ExcludeCanceledFromMetrics && string.Equals(build.Result, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            excludedFromMetrics = true;
            reasons.Add("Build result excluded by metrics inclusion policy");
        }

        if (!policy.EffectiveDurationMetricsResults().Contains(build.Result ?? string.Empty))
        {
            excludedFromMetrics = true;
            if (!string.Equals(build.Result, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Build result excluded by metrics inclusion policy");
            }
        }

        if (filter.Enabled)
        {
            if (stage?.DurationSeconds is { } stageDurationSeconds)
            {
                var durationMinutes = stageDurationSeconds / 60d;
                if (durationMinutes > filter.MaxBuildStageDurationMinutes)
                {
                    var reason = $"Build stage duration exceeded {filter.MaxBuildStageDurationMinutes:0.####} minutes";
                    logger.LogWarning(
                        "Build {BuildId} excluded from metrics as outlier. BuildStageDurationMinutes={BuildStageDurationMinutes}, ThresholdMinutes={ThresholdMinutes}. Reason={Reason}",
                        build.Id,
                        durationMinutes,
                        filter.MaxBuildStageDurationMinutes,
                        filter.Reason);
                    isOutlier = true;
                    excludedFromMetrics |= filter.ExcludeFromMetrics;
                    reasons.Add(reason);
                }
            }

            if (build.QueueDurationSeconds is { } queueDurationSeconds && queueDurationSeconds / 60d > filter.MaxQueueDurationMinutes)
            {
                const string reason = "Queue duration exceeded configured threshold";
                logger.LogWarning(
                    "Build {BuildId} excluded from metrics as queue outlier. QueueDurationMinutes={QueueDurationMinutes}, ThresholdMinutes={ThresholdMinutes}. Reason={Reason}",
                    build.Id,
                    queueDurationSeconds / 60d,
                    filter.MaxQueueDurationMinutes,
                    reason);
                isOutlier = true;
                isQueueOutlier = true;
                excludedFromMetrics |= filter.ExcludeFromMetrics;
                reasons.Add(reason);
            }
        }

        var reasonText = string.Join("; ", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
        return new BuildOutlierStatus(isOutlier, isQueueOutlier, excludedFromMetrics, string.IsNullOrWhiteSpace(reasonText) ? null : reasonText);
    }

    private DateTimeOffset? GetArtifactReadyTime(TimelineStageInfo? stage, IReadOnlyList<TimelineTaskInfo> tasks)
    {
        if (stage is null)
        {
            return null;
        }

        if (!options.Value.MetricsInclusionPolicy.UseArtifactReadyTimeFromPublishTask)
        {
            return stage.FinishTime;
        }

        return tasks
            .Where(static task => task.FinishTime.HasValue && task.TaskName.Contains("Publish", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static task => task.FinishTime)
            .FirstOrDefault()
            ?.FinishTime ?? stage.FinishTime;
    }

    private static double? DurationSeconds(DateTimeOffset? start, DateTimeOffset? end)
    {
        return start is null || end is null ? null : Math.Max(0, (end.Value - start.Value).TotalSeconds);
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

        if (current.OutlierFilter.MaxQueueDurationMinutes <= 0)
        {
            throw new InvalidOperationException("AzureDevOps:OutlierFilter:MaxQueueDurationMinutes must be greater than zero.");
        }
    }

    private sealed record BuildOutlierStatus(bool IsOutlier, bool IsQueueOutlier, bool ExcludedFromMetrics, string? Reason);
}
