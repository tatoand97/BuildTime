using AdoPipelineMetrics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdoPipelineMetrics.Application;

public sealed class RepositoryService(
    IAzureDevOpsClient client,
    IOptions<AzureDevOpsOptions> options,
    ILogger<RepositoryService> logger) : IRepositoryService
{
    public async Task<IReadOnlyList<RepositoryInfo>> GetConfiguredRepositoriesAsync(CancellationToken cancellationToken)
    {
        var repositories = await client.GetRepositoriesAsync(cancellationToken);
        var names = options.Value.RepositoryNames
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (names.Count == 0)
        {
            logger.LogWarning("No RepositoryNames were configured.");
            return [];
        }

        var filtered = repositories.Where(repository => names.Contains(repository.Name)).ToArray();
        foreach (var missing in names.Except(filtered.Select(static repository => repository.Name), StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("Configured repository {RepositoryName} was not found.", missing);
        }

        return filtered;
    }
}

public sealed class PipelineDefinitionService(
    IAzureDevOpsClient client,
    ILogger<PipelineDefinitionService> logger) : IPipelineDefinitionService
{
    public async Task<IReadOnlyList<PipelineDefinitionInfo>> GetDefinitionsAsync(RepositoryInfo repository, CancellationToken cancellationToken)
    {
        var definitions = await client.GetDefinitionsByRepositoryAsync(repository.Id, cancellationToken);
        if (definitions.Count == 0)
        {
            var allDefinitions = await client.GetAllDefinitionsAsync(cancellationToken);
            definitions = allDefinitions
                .Where(definition =>
                    string.Equals(definition.RepositoryId, repository.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(definition.RepositoryName, repository.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (definitions.Count == 0)
        {
            logger.LogWarning("Repository {RepositoryName} has no associated build definitions.", repository.Name);
        }

        return definitions;
    }
}

public sealed class BuildRunService(
    IAzureDevOpsClient client,
    IOptions<AzureDevOpsOptions> options,
    ILogger<BuildRunService> logger) : IBuildRunService
{
    public async Task<IReadOnlyList<BuildRunInfo>> GetBuildsAsync(PipelineDefinitionInfo definition, RepositoryInfo repository, CancellationToken cancellationToken)
    {
        var branches = options.Value.Branches.Count == 0
            ? new[] { "refs/heads/develop", "refs/heads/test" }
            : options.Value.Branches.Where(static branch => !string.IsNullOrWhiteSpace(branch)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var builds = new Dictionary<int, BuildRunInfo>();
        foreach (var branch in branches)
        {
            var branchBuilds = await client.GetBuildsAsync(definition.Id, repository.Id, branch, options.Value.TopBuilds, cancellationToken);
            foreach (var build in branchBuilds)
            {
                builds.TryAdd(build.Id, build);
            }
        }

        if (builds.Count == 0)
        {
            logger.LogWarning("Pipeline {PipelineName} has no builds for repository {RepositoryName}.", definition.Name, repository.Name);
        }

        return builds.Values.OrderByDescending(static build => build.FinishTime ?? build.StartTime ?? build.QueueTime).ToArray();
    }
}

public sealed class TimelineService(
    IAzureDevOpsClient client,
    IOptions<AzureDevOpsOptions> options,
    ILogger<TimelineService> logger) : ITimelineService
{
    public async Task<BuildTimelineExtraction> GetBuildStageAsync(int buildId, CancellationToken cancellationToken)
    {
        var records = await client.GetTimelineRecordsAsync(buildId, cancellationToken);
        var extraction = ExtractBuildStage(records);
        if (extraction.Stage is null)
        {
            logger.LogWarning("Build {BuildId} does not contain stage {StageName}.", buildId, options.Value.StageName);
        }

        return extraction;
    }

    public BuildTimelineExtraction ExtractBuildStage(IReadOnlyList<TimelineRecord> records)
    {
        var stageNames = options.Value.EffectiveStageNames();
        var stage = records.FirstOrDefault(record =>
            string.Equals(record.Type, "Stage", StringComparison.OrdinalIgnoreCase) &&
            stageNames.Contains(record.Name));

        if (stage is null)
        {
            return new BuildTimelineExtraction(null, [], []);
        }

        var descendants = GetDescendants(records, stage.Id).ToArray();
        var jobsById = descendants
            .Where(static record => string.Equals(record.Type, "Job", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static record => record.Id, static record => record);

        var jobs = jobsById.Values
            .Select(job => new TimelineJobInfo(job.Id, stage.Id, job.Name, job.StartTime, job.FinishTime, job.Result, job.State, job.WorkerName))
            .ToArray();

        var tasks = descendants
            .Where(static record => string.Equals(record.Type, "Task", StringComparison.OrdinalIgnoreCase))
            .Select(task =>
            {
                var parentJob = FindParentJob(task, records, jobsById);
                return new TimelineTaskInfo(
                    task.Id,
                    stage.Name,
                    parentJob?.Name,
                    task.Name,
                    task.Task?.Name,
                    task.Task?.Id,
                    task.StartTime,
                    task.FinishTime,
                    task.Result,
                    task.State,
                    task.ErrorCount,
                    task.WarningCount,
                    task.LogId,
                    parentJob?.WorkerName);
            })
            .ToArray();

        return new BuildTimelineExtraction(
            new TimelineStageInfo(stage.Id, stage.Name, stage.StartTime, stage.FinishTime, stage.Result, stage.State),
            jobs,
            tasks);
    }

    private static IEnumerable<TimelineRecord> GetDescendants(IReadOnlyList<TimelineRecord> records, string parentId)
    {
        foreach (var child in records.Where(record => string.Equals(record.ParentId, parentId, StringComparison.OrdinalIgnoreCase)))
        {
            yield return child;
            foreach (var descendant in GetDescendants(records, child.Id))
            {
                yield return descendant;
            }
        }
    }

    private static TimelineRecord? FindParentJob(TimelineRecord task, IReadOnlyList<TimelineRecord> records, IReadOnlyDictionary<string, TimelineRecord> jobsById)
    {
        var currentParentId = task.ParentId;
        while (!string.IsNullOrWhiteSpace(currentParentId))
        {
            if (jobsById.TryGetValue(currentParentId, out var job))
            {
                return job;
            }

            currentParentId = records.FirstOrDefault(record => string.Equals(record.Id, currentParentId, StringComparison.OrdinalIgnoreCase))?.ParentId;
        }

        return null;
    }
}

public sealed class ArtifactService(IAzureDevOpsClient client, IOptions<AzureDevOpsOptions> options) : IArtifactService
{
    public Task<IReadOnlyList<BuildArtifactInfo>> GetArtifactsAsync(int buildId, CancellationToken cancellationToken)
    {
        return client.GetArtifactsAsync(buildId, options.Value.DownloadArtifactsForSize, cancellationToken);
    }
}

public sealed class MetricsCalculator(IOptions<AzureDevOpsOptions>? options = null) : IMetricsCalculator
{
    public IReadOnlyList<BuildMetricsSummary> CalculateBuildSummaries(IReadOnlyList<RepositoryReport> repositories)
    {
        return repositories
            .SelectMany(repository => repository.Pipelines.Select(pipeline => Calculate(repository.RepositoryName, pipeline)))
            .ToArray();
    }

    public IReadOnlyList<TaskMetricsSummary> CalculateTaskSummaries(IReadOnlyList<RepositoryReport> repositories)
    {
        var summaries = new List<TaskMetricsSummary>();
        foreach (var repository in repositories)
        {
            foreach (var pipeline in repository.Pipelines)
            {
                var groups = pipeline.Builds
                    .Where(static build => !build.ExcludedFromMetrics)
                    .SelectMany(static build => build.Tasks)
                    .GroupBy(static task => task.TaskName, StringComparer.OrdinalIgnoreCase);

                summaries.AddRange(groups.Select(group => new TaskMetricsSummary(
                    repository.RepositoryName,
                    pipeline.DefinitionName,
                    group.Key,
                    group.Count(),
                    Average(group.Select(static task => task.DurationSeconds)))));
            }
        }

        return summaries;
    }

    public double? Percentile(IEnumerable<double?> values, double percentile)
    {
        var ordered = values.Where(static value => value.HasValue).Select(static value => value!.Value).Order().ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        if (ordered.Length == 1)
        {
            return ordered[0];
        }

        var rank = (percentile / 100d) * (ordered.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return ordered[lower];
        }

        return ordered[lower] + ((ordered[upper] - ordered[lower]) * (rank - lower));
    }

    private BuildMetricsSummary Calculate(string repositoryName, PipelineReport pipeline)
    {
        var policy = options?.Value.MetricsInclusionPolicy ?? new MetricsInclusionPolicyOptions();
        var durationResults = policy.EffectiveDurationMetricsResults();
        var failureRateResults = policy.EffectiveFailureRateResults();
        var durationBuilds = pipeline.Builds
            .Where(build => !build.ExcludedFromMetrics && durationResults.Contains(build.Result ?? string.Empty))
            .ToArray();
        var failureRateBuilds = pipeline.Builds
            .Where(build => failureRateResults.Contains(build.Result ?? string.Empty))
            .Where(build => !policy.ExcludeCanceledFromMetrics || !string.Equals(build.Result, "canceled", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var buildsFetched = pipeline.Builds.Count;
        var buildsExcludedAsOutliers = pipeline.Builds.Count(static build => build.IsOutlier && build.ExcludedFromMetrics);
        var buildsExcludedMissingStage = pipeline.Builds.Count(static build => build.ExcludedFromMetrics && build.BuildStageDurationSeconds is null);
        var buildsExcludedByResult = pipeline.Builds.Count(build => !durationResults.Contains(build.Result ?? string.Empty));
        var artifactSizes = durationBuilds.SelectMany(static build => build.Artifacts).Select(static artifact => artifact.SizeMb).Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        var successfulBuilds = failureRateBuilds.Count(static build => string.Equals(build.Result, "succeeded", StringComparison.OrdinalIgnoreCase));
        var failedBuilds = failureRateBuilds.Count(static build => string.Equals(build.Result, "failed", StringComparison.OrdinalIgnoreCase));
        var failureRateDenominator = successfulBuilds + failedBuilds;
        return new BuildMetricsSummary(
            repositoryName,
            pipeline.DefinitionName,
            durationBuilds.Length,
            buildsFetched,
            durationBuilds.Length,
            durationBuilds.Count(static build => string.Equals(build.Result, "succeeded", StringComparison.OrdinalIgnoreCase)),
            failedBuilds,
            pipeline.Builds.Count(static build => string.Equals(build.Result, "canceled", StringComparison.OrdinalIgnoreCase)),
            buildsExcludedMissingStage,
            buildsExcludedAsOutliers,
            buildsExcludedByResult,
            options?.Value.OutlierFilter.Enabled == true ? options.Value.OutlierFilter.MaxBuildStageDurationMinutes : null,
            successfulBuilds,
            failedBuilds,
            failureRateDenominator == 0 ? 0 : (double)failedBuilds / failureRateDenominator,
            Average(durationBuilds.Select(static build => build.BuildStageDurationSeconds)),
            Percentile(durationBuilds.Select(static build => build.BuildStageDurationSeconds), 50),
            Percentile(durationBuilds.Select(static build => build.BuildStageDurationSeconds), 90),
            Percentile(durationBuilds.Select(static build => build.BuildStageDurationSeconds), 99),
            Average(durationBuilds.Select(static build => build.QueueDurationSeconds)),
            Average(durationBuilds.Select(static build => build.QueueToFinishDurationSeconds)),
            Average(durationBuilds.Select(static build => build.QueueToArtifactReadySeconds)),
            artifactSizes.Length == 0 ? null : artifactSizes.Average(),
            artifactSizes.Length == 0 ? null : artifactSizes.Max(),
            artifactSizes.Length == 0 ? null : artifactSizes.Min());
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var materialized = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return materialized.Length == 0 ? null : materialized.Average();
    }
}
