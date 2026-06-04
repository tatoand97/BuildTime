using AdoPipelineMetrics.Domain;

namespace AdoPipelineMetrics.Application;

public interface IAzureDevOpsClient
{
    Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PipelineDefinitionInfo>> GetDefinitionsByRepositoryAsync(string repositoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PipelineDefinitionInfo>> GetAllDefinitionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BuildRunInfo>> GetBuildsAsync(int definitionId, string repositoryId, string branchName, int topBuilds, CancellationToken cancellationToken);
    Task<IReadOnlyList<TimelineRecord>> GetTimelineRecordsAsync(int buildId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BuildArtifactInfo>> GetArtifactsAsync(int buildId, bool downloadArtifactsForSize, CancellationToken cancellationToken);
}

public interface IRepositoryService
{
    Task<IReadOnlyList<RepositoryInfo>> GetConfiguredRepositoriesAsync(CancellationToken cancellationToken);
}

public interface IPipelineDefinitionService
{
    Task<IReadOnlyList<PipelineDefinitionInfo>> GetDefinitionsAsync(RepositoryInfo repository, CancellationToken cancellationToken);
}

public interface IBuildRunService
{
    Task<IReadOnlyList<BuildRunInfo>> GetBuildsAsync(PipelineDefinitionInfo definition, RepositoryInfo repository, CancellationToken cancellationToken);
}

public interface ITimelineService
{
    Task<BuildTimelineExtraction> GetBuildStageAsync(int buildId, CancellationToken cancellationToken);
    BuildTimelineExtraction ExtractBuildStage(IReadOnlyList<TimelineRecord> records);
}

public interface IArtifactService
{
    Task<IReadOnlyList<BuildArtifactInfo>> GetArtifactsAsync(int buildId, CancellationToken cancellationToken);
}

public interface IMetricsCalculator
{
    IReadOnlyList<BuildMetricsSummary> CalculateBuildSummaries(IReadOnlyList<RepositoryReport> repositories);
    IReadOnlyList<TaskMetricsSummary> CalculateTaskSummaries(IReadOnlyList<RepositoryReport> repositories);
    double? Percentile(IEnumerable<double?> values, double percentile);
}

public interface ICsvExporter
{
    Task ExportAsync(ExtractionResult result, string outputPath, CancellationToken cancellationToken);
}

public interface IJsonExporter
{
    Task ExportAsync(ExtractionResult result, string outputPath, CancellationToken cancellationToken);
}

public interface IPipelineMetricsRunner
{
    Task<ExtractionResult> RunAsync(CancellationToken cancellationToken);
}

public sealed record TimelineRecord(
    string Id,
    string? ParentId,
    string Type,
    string Name,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    string? Result,
    string? State,
    int ErrorCount,
    int WarningCount,
    int? LogId,
    string? WorkerName,
    TimelineTaskReference? Task);

public sealed record TimelineTaskReference(string? Id, string? Name);

public sealed record BuildTimelineExtraction(
    TimelineStageInfo? Stage,
    IReadOnlyList<TimelineJobInfo> Jobs,
    IReadOnlyList<TimelineTaskInfo> Tasks);
