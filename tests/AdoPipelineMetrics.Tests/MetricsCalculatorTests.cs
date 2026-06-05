using AdoPipelineMetrics.Application;
using AdoPipelineMetrics.Domain;

namespace AdoPipelineMetrics.Tests;

public sealed class MetricsCalculatorTests
{
    [Fact]
    public void BuildRunInfo_CalculatesDurationsInSeconds()
    {
        var queued = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var started = queued.AddMinutes(2);
        var finished = started.AddMinutes(8);

        var build = new BuildRunInfo(1, "20260101.1", 10, "pipeline", "repo-id", "repo", "refs/heads/develop", "abc", "manual", "completed", "succeeded", queued, started, finished);

        Assert.Equal(120, build.QueueDurationSeconds);
        Assert.Equal(480, build.BuildDurationSeconds);
        Assert.Equal(600, build.QueueToFinishDurationSeconds);
    }

    [Fact]
    public void Percentile_InterpolatesP50P90AndP99()
    {
        var calculator = new MetricsCalculator();
        double?[] values = [10, 20, 30, 40, 50];

        Assert.Equal(30, calculator.Percentile(values, 50));
        Assert.Equal(46, calculator.Percentile(values, 90));
        var p99 = calculator.Percentile(values, 99);
        Assert.NotNull(p99);
        Assert.Equal(49.6, p99.Value, precision: 5);
    }

    [Fact]
    public void CalculateBuildSummaries_CalculatesArtifactSizes()
    {
        var calculator = new MetricsCalculator();
        var result = calculator.CalculateBuildSummaries([
            new RepositoryReport("repo-a", "repo-id", [
                new PipelineReport(1, "pipeline-a", [
                    new BuildReport(1, "1", "refs/heads/develop", "completed", "succeeded", null, null, null, 10, 20, 30, 40, false, false, null, [], [
                        new BuildArtifactInfo("drop", "Container", 1048576, 1, "FromProperties", null, null, new Dictionary<string, string?>())
                    ]),
                    new BuildReport(2, "2", "refs/heads/develop", "completed", "failed", null, null, null, 20, 30, 50, 60, false, false, null, [], [
                        new BuildArtifactInfo("drop", "Container", 2097152, 2, "FromProperties", null, null, new Dictionary<string, string?>())
                    ])
                ])
            ])
        ]);

        var summary = Assert.Single(result);
        Assert.Equal(2, summary.BuildsAnalyzed);
        Assert.Equal(2, summary.BuildsFetched);
        Assert.Equal(2, summary.BuildsIncludedInMetrics);
        Assert.Equal(0, summary.BuildsExcludedAsOutliers);
        Assert.Equal(1, summary.SuccessfulBuilds);
        Assert.Equal(1, summary.FailedBuilds);
        Assert.Equal(0.5, summary.FailureRate);
        Assert.Equal(1.5, summary.AverageArtifactSizeMb);
        Assert.Equal(2, summary.MaxArtifactSizeMb);
        Assert.Equal(1, summary.MinArtifactSizeMb);
    }

    [Fact]
    public void CalculateBuildSummaries_ExcludesOutliersFromMetrics()
    {
        var calculator = new MetricsCalculator();
        var result = calculator.CalculateBuildSummaries([
            new RepositoryReport("repo-a", "repo-id", [
                new PipelineReport(1, "pipeline-a", [
                    new BuildReport(1, "1", "refs/heads/develop", "completed", "succeeded", null, null, null, 10, 20, 30, 300, false, false, null, [], []),
                    new BuildReport(2, "2", "refs/heads/develop", "completed", "failed", null, null, null, 20, 30, 50, 1200, true, true, "Build stage duration exceeded 15 minutes", [], [])
                ])
            ])
        ]);

        var summary = Assert.Single(result);
        Assert.Equal(1, summary.BuildsAnalyzed);
        Assert.Equal(2, summary.BuildsFetched);
        Assert.Equal(1, summary.BuildsIncludedInMetrics);
        Assert.Equal(1, summary.BuildsExcludedAsOutliers);
        Assert.Equal(1, summary.SuccessfulBuilds);
        Assert.Equal(0, summary.FailedBuilds);
        Assert.Equal(0, summary.FailureRate);
        Assert.Equal(300, summary.AverageBuildStageDurationSeconds);
        Assert.Equal(30, summary.AverageQueueToFinishSeconds);
    }
}
