namespace AdoPipelineMetrics.Application;

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    public string Organization { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string? PersonalAccessToken { get; set; }
    public string? PersonalAccessTokenEnvironmentVariable { get; set; } = "AZDO_PAT";
    public List<string> RepositoryNames { get; set; } = [];
    public List<string> Branches { get; set; } = ["refs/heads/develop", "refs/heads/test"];
    public int TopBuilds { get; set; } = 60;
    public string StageName { get; set; } = "Build";
    public List<string> StageNameAliases { get; set; } = [];
    public bool DownloadArtifactsForSize { get; set; }
    public string OutputPath { get; set; } = "./output";
    public MetricsInclusionPolicyOptions MetricsInclusionPolicy { get; set; } = new();
    public OutlierFilterOptions OutlierFilter { get; set; } = new();

    public IReadOnlySet<string> EffectiveStageNames()
    {
        return new[] { StageName }
            .Concat(StageNameAliases)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToHashSet(StringComparer.Ordinal);
    }
}

public sealed class MetricsInclusionPolicyOptions
{
    public List<string> DurationMetricsResults { get; set; } = ["succeeded"];
    public List<string> FailureRateResults { get; set; } = ["succeeded", "failed"];
    public bool ExcludeCanceledFromMetrics { get; set; } = true;
    public bool ExcludeMissingStageFromMetrics { get; set; } = true;
    public bool UseArtifactReadyTimeFromPublishTask { get; set; } = true;

    public IReadOnlySet<string> EffectiveDurationMetricsResults()
    {
        return ToResultSet(DurationMetricsResults);
    }

    public IReadOnlySet<string> EffectiveFailureRateResults()
    {
        return ToResultSet(FailureRateResults);
    }

    private static IReadOnlySet<string> ToResultSet(IEnumerable<string> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class OutlierFilterOptions
{
    public bool Enabled { get; set; } = true;
    public double MaxBuildStageDurationMinutes { get; set; } = 15;
    public double MaxQueueDurationMinutes { get; set; } = 15;
    public bool ExcludeFromMetrics { get; set; } = true;
    public bool KeepInRawJson { get; set; } = true;
    public string Reason { get; set; } = "Build stage duration exceeded configured threshold";
}
