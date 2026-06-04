using AdoPipelineMetrics.Application;
using AdoPipelineMetrics.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Any(static arg => arg is "--help" or "-h"))
{
    PrintHelp();
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddInMemoryCollection(ParseOverrides(args));

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services.AddAdoPipelineMetrics(builder.Configuration);

using var host = builder.Build();
var runner = host.Services.GetRequiredService<IPipelineMetricsRunner>();
await runner.RunAsync(CancellationToken.None);
return 0;

static Dictionary<string, string?> ParseOverrides(string[] args)
{
    var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    for (var index = 0; index < args.Length; index++)
    {
        var arg = args[index];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = arg[2..];
        var value = "true";
        var equalsIndex = key.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex >= 0)
        {
            value = key[(equalsIndex + 1)..];
            key = key[..equalsIndex];
        }
        else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = args[++index];
        }

        switch (key)
        {
            case "repos":
                AddArray(overrides, "AzureDevOps:RepositoryNames", SplitCsv(value));
                break;
            case "branches":
                AddArray(overrides, "AzureDevOps:Branches", SplitCsv(value));
                break;
            case "top-builds":
                overrides["AzureDevOps:TopBuilds"] = value;
                break;
            case "stage-name":
                overrides["AzureDevOps:StageName"] = value;
                break;
            case "stage-name-aliases":
                AddArray(overrides, "AzureDevOps:StageNameAliases", SplitCsv(value));
                break;
            case "output":
                overrides["AzureDevOps:OutputPath"] = value;
                break;
            case "download-artifacts-for-size":
                overrides["AzureDevOps:DownloadArtifactsForSize"] = value;
                break;
        }
    }

    return overrides;
}

static string[] SplitCsv(string value)
{
    return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static void AddArray(IDictionary<string, string?> target, string key, IReadOnlyList<string> values)
{
    for (var index = 0; index < values.Count; index++)
    {
        target[$"{key}:{index}"] = values[index];
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
    AdoPipelineMetrics

    Usage:
      dotnet run --project src/AdoPipelineMetrics.Cli -- [options]

    Options:
      --repos repo-a,repo-b
      --branches refs/heads/develop,refs/heads/test
      --top-builds 60
      --stage-name Build
      --stage-name-aliases "Build,Build Project"
      --output ./output
      --download-artifacts-for-size false
      --help

    Configuration is read from appsettings.json, environment variables, and CLI overrides.
    The Azure DevOps PAT is read from AzureDevOps:PersonalAccessTokenEnvironmentVariable or AzureDevOps:PersonalAccessToken.
    """);
}
