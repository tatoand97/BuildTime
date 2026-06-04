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

    public IReadOnlySet<string> EffectiveStageNames()
    {
        return new[] { StageName }
            .Concat(StageNameAliases)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToHashSet(StringComparer.Ordinal);
    }
}
