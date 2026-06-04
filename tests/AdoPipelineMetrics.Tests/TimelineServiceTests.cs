using AdoPipelineMetrics.Application;
using AdoPipelineMetrics.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AdoPipelineMetrics.Tests;

public sealed class TimelineServiceTests
{
    [Fact]
    public void ExtractBuildStage_UsesConfiguredStageNameAndParentIdHierarchy()
    {
        var service = CreateService(new AzureDevOpsOptions { StageName = "Build" });
        var records = CreateRecords(buildStageName: "Build");

        var result = service.ExtractBuildStage(records);

        Assert.NotNull(result.Stage);
        Assert.Equal("Build", result.Stage.Name);
        Assert.Single(result.Jobs);
        var task = Assert.Single(result.Tasks);
        Assert.Equal("Compile FE", task.TaskName);
        Assert.Equal("Build Job", task.JobName);
        Assert.Equal("agent-01", task.WorkerName);
    }

    [Fact]
    public void ExtractBuildStage_UsesAliasesAndDoesNotIncludeDeployTasks()
    {
        var service = CreateService(new AzureDevOpsOptions { StageName = "Build", StageNameAliases = ["Build Project"] });
        var records = CreateRecords(buildStageName: "Build Project");

        var result = service.ExtractBuildStage(records);

        Assert.NotNull(result.Stage);
        Assert.Equal("Build Project", result.Stage.Name);
        Assert.DoesNotContain(result.Tasks, task => task.TaskName.Contains("Deploy", StringComparison.OrdinalIgnoreCase));
    }

    private static TimelineService CreateService(AzureDevOpsOptions options)
    {
        return new TimelineService(new EmptyAzureDevOpsClient(), Options.Create(options), NullLogger<TimelineService>.Instance);
    }

    private static List<TimelineRecord> CreateRecords(string buildStageName)
    {
        var start = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        return
        [
            new TimelineRecord("stage-build", null, "Stage", buildStageName, start, start.AddMinutes(10), "succeeded", "completed", 0, 0, null, null, null),
            new TimelineRecord("job-build", "stage-build", "Job", "Build Job", start, start.AddMinutes(10), "succeeded", "completed", 0, 0, null, "agent-01", null),
            new TimelineRecord("phase-build", "job-build", "Phase", "Build Phase", start, start.AddMinutes(10), "succeeded", "completed", 0, 0, null, null, null),
            new TimelineRecord("task-compile", "phase-build", "Task", "Compile FE", start, start.AddMinutes(3), "succeeded", "completed", 0, 1, 22, null, new TimelineTaskReference("task-id", "DotNetCoreCLI")),
            new TimelineRecord("stage-deploy", null, "Stage", "Deploy", start, start.AddMinutes(5), "succeeded", "completed", 0, 0, null, null, null),
            new TimelineRecord("job-deploy", "stage-deploy", "Job", "Deploy Job", start, start.AddMinutes(5), "succeeded", "completed", 0, 0, null, "agent-02", null),
            new TimelineRecord("task-deploy", "job-deploy", "Task", "Deploy App", start, start.AddMinutes(2), "succeeded", "completed", 0, 0, 33, null, new TimelineTaskReference("deploy-id", "AzureWebApp"))
        ];
    }

    private sealed class EmptyAzureDevOpsClient : IAzureDevOpsClient
    {
        public Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RepositoryInfo>>([]);
        public Task<IReadOnlyList<PipelineDefinitionInfo>> GetDefinitionsByRepositoryAsync(string repositoryId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PipelineDefinitionInfo>>([]);
        public Task<IReadOnlyList<PipelineDefinitionInfo>> GetAllDefinitionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PipelineDefinitionInfo>>([]);
        public Task<IReadOnlyList<BuildRunInfo>> GetBuildsAsync(int definitionId, string repositoryId, string branchName, int topBuilds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BuildRunInfo>>([]);
        public Task<IReadOnlyList<TimelineRecord>> GetTimelineRecordsAsync(int buildId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TimelineRecord>>([]);
        public Task<IReadOnlyList<BuildArtifactInfo>> GetArtifactsAsync(int buildId, bool downloadArtifactsForSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BuildArtifactInfo>>([]);
    }
}
