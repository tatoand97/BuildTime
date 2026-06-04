using System.Globalization;
using System.Text;
using System.Text.Json;
using AdoPipelineMetrics.Application;
using AdoPipelineMetrics.Domain;

namespace AdoPipelineMetrics.Infrastructure;

public sealed class JsonExporter : IJsonExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task ExportAsync(ExtractionResult result, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputPath);
        await using var stream = File.Create(Path.Combine(outputPath, "pipeline-metrics.json"));
        await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
    }
}

public sealed class CsvExporter : ICsvExporter
{
    public async Task ExportAsync(ExtractionResult result, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputPath);
        await WriteBuildSummaryAsync(result, Path.Combine(outputPath, "builds-summary.csv"), cancellationToken);
        await WriteTasksAsync(result, Path.Combine(outputPath, "build-tasks.csv"), cancellationToken);
        await WriteArtifactsAsync(result, Path.Combine(outputPath, "artifacts.csv"), cancellationToken);
        await WriteMetricsAsync(result, Path.Combine(outputPath, "metrics-summary.csv"), cancellationToken);
    }

    private static Task WriteBuildSummaryAsync(ExtractionResult result, string path, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>
        {
            new[] { "repositoryName", "pipelineId", "pipelineName", "buildId", "buildNumber", "branch", "status", "result", "queueTime", "startTime", "finishTime", "queueDurationSeconds", "buildDurationSeconds", "queueToFinishDurationSeconds", "buildStageDurationSeconds", "artifactTotalSizeMb" }
        };

        foreach (var item in EnumerateBuilds(result))
        {
            rows.Add([
                item.Repository.RepositoryName,
                Format(item.Pipeline.DefinitionId),
                item.Pipeline.DefinitionName,
                Format(item.Build.BuildId),
                item.Build.BuildNumber,
                item.Build.Branch ?? string.Empty,
                item.Build.Status ?? string.Empty,
                item.Build.Result ?? string.Empty,
                Format(item.Build.QueueTime),
                Format(item.Build.StartTime),
                Format(item.Build.FinishTime),
                Format(item.Build.QueueDurationSeconds),
                Format(item.Build.BuildDurationSeconds),
                Format(item.Build.QueueToFinishDurationSeconds),
                Format(item.Build.BuildStageDurationSeconds),
                Format(SumArtifactSizeMb(item.Build.Artifacts))
            ]);
        }

        return WriteRowsAsync(path, rows, cancellationToken);
    }

    private static Task WriteTasksAsync(ExtractionResult result, string path, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>
        {
            new[] { "repositoryName", "pipelineId", "pipelineName", "buildId", "buildNumber", "branch", "stageName", "jobName", "taskName", "taskTypeName", "startTime", "finishTime", "durationSeconds", "result", "state", "errorCount", "warningCount", "workerName", "logId" }
        };

        foreach (var item in EnumerateBuilds(result))
        {
            foreach (var task in item.Build.Tasks)
            {
                rows.Add([
                    item.Repository.RepositoryName,
                    Format(item.Pipeline.DefinitionId),
                    item.Pipeline.DefinitionName,
                    Format(item.Build.BuildId),
                    item.Build.BuildNumber,
                    item.Build.Branch ?? string.Empty,
                    task.StageName,
                    task.JobName ?? string.Empty,
                    task.TaskName,
                    task.TaskTypeName ?? string.Empty,
                    Format(task.StartTime),
                    Format(task.FinishTime),
                    Format(task.DurationSeconds),
                    task.Result ?? string.Empty,
                    task.State ?? string.Empty,
                    Format(task.ErrorCount),
                    Format(task.WarningCount),
                    task.WorkerName ?? string.Empty,
                    task.LogId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
                ]);
            }
        }

        return WriteRowsAsync(path, rows, cancellationToken);
    }

    private static Task WriteArtifactsAsync(ExtractionResult result, string path, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>
        {
            new[] { "repositoryName", "pipelineId", "pipelineName", "buildId", "buildNumber", "artifactName", "artifactType", "sizeBytes", "sizeMb", "sizeStatus", "downloadUrl" }
        };

        foreach (var item in EnumerateBuilds(result))
        {
            foreach (var artifact in item.Build.Artifacts)
            {
                rows.Add([
                    item.Repository.RepositoryName,
                    Format(item.Pipeline.DefinitionId),
                    item.Pipeline.DefinitionName,
                    Format(item.Build.BuildId),
                    item.Build.BuildNumber,
                    artifact.Name,
                    artifact.Type ?? string.Empty,
                    artifact.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Format(artifact.SizeMb),
                    artifact.SizeStatus,
                    artifact.DownloadUrl ?? string.Empty
                ]);
            }
        }

        return WriteRowsAsync(path, rows, cancellationToken);
    }

    private static Task WriteMetricsAsync(ExtractionResult result, string path, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>
        {
            new[] { "repositoryName", "pipelineName", "buildsAnalyzed", "successfulBuilds", "failedBuilds", "failureRate", "averageBuildStageDurationSeconds", "p50BuildStageDurationSeconds", "p90BuildStageDurationSeconds", "p99BuildStageDurationSeconds", "averageQueueDurationSeconds", "averageQueueToFinishSeconds", "averageArtifactSizeMb", "maxArtifactSizeMb", "minArtifactSizeMb" }
        };

        foreach (var metric in result.Metrics)
        {
            rows.Add([
                metric.RepositoryName,
                metric.PipelineName,
                Format(metric.BuildsAnalyzed),
                Format(metric.SuccessfulBuilds),
                Format(metric.FailedBuilds),
                Format(metric.FailureRate),
                Format(metric.AverageBuildStageDurationSeconds),
                Format(metric.P50BuildStageDurationSeconds),
                Format(metric.P90BuildStageDurationSeconds),
                Format(metric.P99BuildStageDurationSeconds),
                Format(metric.AverageQueueDurationSeconds),
                Format(metric.AverageQueueToFinishSeconds),
                Format(metric.AverageArtifactSizeMb),
                Format(metric.MaxArtifactSizeMb),
                Format(metric.MinArtifactSizeMb)
            ]);
        }

        return WriteRowsAsync(path, rows, cancellationToken);
    }

    private static IEnumerable<(RepositoryReport Repository, PipelineReport Pipeline, BuildReport Build)> EnumerateBuilds(ExtractionResult result)
    {
        return result.Repositories.SelectMany(repository =>
            repository.Pipelines.SelectMany(pipeline =>
                pipeline.Builds.Select(build => (repository, pipeline, build))));
    }

    private static double? SumArtifactSizeMb(IReadOnlyList<BuildArtifactInfo> artifacts)
    {
        var sizes = artifacts.Select(static artifact => artifact.SizeMb).Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return sizes.Length == 0 ? null : sizes.Sum();
    }

    private static async Task WriteRowsAsync(string path, IReadOnlyList<string[]> rows, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(Escape)));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string Format(DateTimeOffset? value)
    {
        return value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Format(double? value)
    {
        return value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Format(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string Format(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
