namespace AdoPipelineMetrics.Domain;

public sealed record RepositoryInfo(string Id, string Name);

public sealed record PipelineDefinitionInfo(int Id, string Name, string? RepositoryId, string? RepositoryName);

public sealed record BuildRunInfo(
    int Id,
    string BuildNumber,
    int DefinitionId,
    string DefinitionName,
    string? RepositoryId,
    string? RepositoryName,
    string? SourceBranch,
    string? SourceVersion,
    string? Reason,
    string? Status,
    string? Result,
    DateTimeOffset? QueueTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime)
{
    public double? QueueDurationSeconds => DurationSeconds(QueueTime, StartTime);
    public double? BuildDurationSeconds => DurationSeconds(StartTime, FinishTime);
    public double? QueueToFinishDurationSeconds => DurationSeconds(QueueTime, FinishTime);

    private static double? DurationSeconds(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null || end is null)
        {
            return null;
        }

        return Math.Max(0, (end.Value - start.Value).TotalSeconds);
    }
}

public sealed record TimelineStageInfo(
    string Id,
    string Name,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    string? Result,
    string? State)
{
    public double? DurationSeconds => StartTime is null || FinishTime is null
        ? null
        : Math.Max(0, (FinishTime.Value - StartTime.Value).TotalSeconds);
}

public sealed record TimelineJobInfo(
    string Id,
    string StageId,
    string Name,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    string? Result,
    string? State,
    string? WorkerName);

public sealed record TimelineTaskInfo(
    string Id,
    string StageName,
    string? JobName,
    string TaskName,
    string? TaskTypeName,
    string? TaskTypeId,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    string? Result,
    string? State,
    int ErrorCount,
    int WarningCount,
    int? LogId,
    string? WorkerName)
{
    public double? DurationSeconds => StartTime is null || FinishTime is null
        ? null
        : Math.Max(0, (FinishTime.Value - StartTime.Value).TotalSeconds);
}

public sealed record BuildArtifactInfo(
    string Name,
    string? Type,
    long? SizeBytes,
    double? SizeMb,
    string SizeStatus,
    string? DownloadUrl,
    string? Data,
    IReadOnlyDictionary<string, string?> Properties);

public sealed record BuildReport(
    int BuildId,
    string BuildNumber,
    string? Branch,
    string? Status,
    string? Result,
    DateTimeOffset? QueueTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    double? QueueDurationSeconds,
    double? BuildDurationSeconds,
    double? QueueToFinishDurationSeconds,
    DateTimeOffset? BuildStageStartTime,
    DateTimeOffset? BuildStageFinishTime,
    DateTimeOffset? ArtifactReadyTime,
    double? BuildStageDurationSeconds,
    double? QueueToArtifactReadySeconds,
    bool IsOutlier,
    bool IsQueueOutlier,
    bool ExcludedFromMetrics,
    string? OutlierReason,
    IReadOnlyList<TimelineTaskInfo> Tasks,
    IReadOnlyList<BuildArtifactInfo> Artifacts);

public sealed record PipelineReport(
    int DefinitionId,
    string DefinitionName,
    IReadOnlyList<BuildReport> Builds);

public sealed record RepositoryReport(
    string RepositoryName,
    string RepositoryId,
    IReadOnlyList<PipelineReport> Pipelines);

public sealed record BuildMetricsSummary(
    string RepositoryName,
    string PipelineName,
    int BuildsAnalyzed,
    int BuildsFetched,
    int BuildsIncludedInMetrics,
    int BuildsSucceededIncludedInDurationMetrics,
    int BuildsFailedForFailureRate,
    int BuildsCanceled,
    int BuildsExcludedMissingStage,
    int BuildsExcludedAsOutliers,
    int BuildsExcludedByResult,
    double? OutlierThresholdMinutes,
    int SuccessfulBuilds,
    int FailedBuilds,
    double FailureRate,
    double? AverageBuildStageDurationSeconds,
    double? P50BuildStageDurationSeconds,
    double? P90BuildStageDurationSeconds,
    double? P99BuildStageDurationSeconds,
    double? AverageQueueDurationSeconds,
    double? AverageQueueToFinishSeconds,
    double? AverageQueueToArtifactReadySeconds,
    double? AverageArtifactSizeMb,
    double? MaxArtifactSizeMb,
    double? MinArtifactSizeMb);

public sealed record TaskMetricsSummary(
    string RepositoryName,
    string PipelineName,
    string TaskName,
    int Count,
    double? AverageDurationSeconds);

public sealed record ExtractionResult(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<RepositoryReport> Repositories,
    IReadOnlyList<BuildMetricsSummary> Metrics,
    IReadOnlyList<TaskMetricsSummary> TaskMetrics);
