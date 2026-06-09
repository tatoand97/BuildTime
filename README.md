# AdoPipelineMetrics

CLI .NET 10 para extraer metricas de compilacion desde Azure DevOps REST API y generar reportes JSON/CSV de pipelines monoliticos FE+BE.

## Requisitos

- .NET SDK 10.
- Un Personal Access Token de Azure DevOps con permisos minimos:
  - Code: Read
  - Build: Read

El PAT se lee desde una variable de entorno o desde `appsettings.json`. No debe quedar hardcodeado en el codigo.

## Configuracion

Copie `appsettings.example.json` como `appsettings.json` y ajuste:

```json
{
  "AzureDevOps": {
    "Organization": "my-org",
    "Project": "my-project",
    "PersonalAccessTokenEnvironmentVariable": "AZDO_PAT",
    "RepositoryNames": ["repo-a", "repo-b"],
    "Branches": ["refs/heads/develop", "refs/heads/test"],
    "TopBuilds": 60,
    "StageName": "Build",
    "StageNameAliases": ["Build Project"],
    "MetricsInclusionPolicy": {
      "DurationMetricsResults": ["succeeded"],
      "FailureRateResults": ["succeeded", "failed"],
      "ExcludeCanceledFromMetrics": true,
      "ExcludeMissingStageFromMetrics": true,
      "UseArtifactReadyTimeFromPublishTask": true
    },
    "OutlierFilter": {
      "Enabled": true,
      "MaxBuildStageDurationMinutes": 15,
      "MaxQueueDurationMinutes": 15,
      "ExcludeFromMetrics": true,
      "KeepInRawJson": true,
      "Reason": "Build stage duration exceeded configured threshold"
    },
    "DownloadArtifactsForSize": false,
    "OutputPath": "./output"
  }
}
```

En PowerShell:

```powershell
$env:AZDO_PAT = "your-pat"
```

## Ejecucion

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/AdoPipelineMetrics.Cli
```

Tambien puede sobrescribir configuracion con flags:

```powershell
dotnet run --project src/AdoPipelineMetrics.Cli -- --repos repo-a,repo-b --branches refs/heads/develop,refs/heads/test --top-builds 60 --stage-name Build --stage-name-aliases "Build Project" --output ./output
```

## Reglas de extraccion

- Solo se analiza el stage cuyo `type` es `Stage` y cuyo `name` coincide exactamente con `StageName` o algun valor de `StageNameAliases`.
- Los jobs y tasks se obtienen recorriendo descendientes por `parentId` desde el stage seleccionado.
- No se incluyen tasks de Deploy, Post-deploy u otros stages.
- Para varias branches se ejecuta una consulta Builds List por cada `branchName`, porque Azure DevOps no acepta arrays en ese parametro.
- Los builds duplicados entre consultas se deduplican por `build.id`.
- Si no se encuentra stage Build, pipelines, builds o tamanio de artefacto, se registra warning y la ejecucion continua.
- `TopBuilds` indica cuantas ejecuciones se consultan desde Azure DevOps; no representa necesariamente cuantas ejecuciones quedan incluidas en las metricas finales.
- `buildDurationSeconds` y `queueToFinishDurationSeconds` se mantienen como datos crudos del build completo.
- Las metricas ADR de duracion usan `buildStageDurationSeconds`, calculado con `startTime`/`finishTime` del stage Build.
- `artifactReadyTime` usa el `finishTime` de una task del stage Build cuyo nombre contenga `Publish`; si no existe, usa el `finishTime` del stage Build.
- `queueToArtifactReadySeconds` se calcula con `artifactReadyTime - queueTime` y reemplaza a queue-to-finish como metrica valida de queue hasta artefacto listo.
- Para metricas de duracion solo se incluyen resultados configurados en `MetricsInclusionPolicy:DurationMetricsResults`, por defecto `succeeded`.
- `failureRate` se calcula como `failedBuilds / (successfulBuilds + failedBuilds)` con resultados configurados en `FailureRateResults`; los cancelados quedan fuera del denominador.
- Si `OutlierFilter.Enabled=true`, la duracion usada para detectar outliers se calcula con `startTime` y `finishTime` del timeline record cuyo `type == "Stage"` y cuyo `name` coincide con `StageName` o sus alias. No se usa `finishTime - queueTime` del build completo.
- Si la duracion del stage Build supera `MaxBuildStageDurationMinutes`, la ejecucion queda marcada con `isOutlier=true`, `excludedFromMetrics=true` y `outlierReason="Build stage duration exceeded {threshold} minutes"`.
- Si `queueDurationSeconds` supera `MaxQueueDurationMinutes`, la ejecucion queda marcada con `isQueueOutlier=true`, `excludedFromMetrics=true` y `outlierReason="Queue duration exceeded configured threshold"`.
- Si el stage Build no existe o no tiene `startTime`/`finishTime`, la ejecucion queda marcada con `excludedFromMetrics=true` y una razon especifica.
- Cuando `KeepInRawJson=true`, los outliers excluidos se conservan en `pipeline-metrics.json`, pero no participan en promedios, percentiles, promedios por task ni metricas de duracion.

## Tamanio de artefactos

El tamanio se obtiene en este orden:

1. `resource.properties`
2. `HEAD downloadUrl` leyendo `Content-Length`
3. descarga opcional por stream si `DownloadArtifactsForSize=true`

Si no se puede obtener, se registra `sizeStatus = "UnavailableFromApi"`.

## Archivos generados

En `OutputPath` se generan:

- `pipeline-metrics.json`
- `builds-summary.csv`
- `build-tasks.csv`
- `artifacts.csv`
- `metrics-summary.csv`

## Ejemplo JSON

```json
{
  "generatedAt": "2026-06-04T12:00:00Z",
  "repositories": [
    {
      "repositoryName": "repo-a",
      "repositoryId": "00000000-0000-0000-0000-000000000000",
      "pipelines": [
        {
          "definitionId": 15,
          "definitionName": "repo-a-ci",
          "builds": [
            {
              "buildId": 1001,
              "buildNumber": "20260604.1",
              "branch": "refs/heads/develop",
              "status": "completed",
              "result": "succeeded",
              "queueDurationSeconds": 12,
              "buildDurationSeconds": 420,
              "queueToFinishDurationSeconds": 432,
              "buildStageStartTime": "2026-06-04T12:00:15Z",
              "buildStageFinishTime": "2026-06-04T12:06:45Z",
              "artifactReadyTime": "2026-06-04T12:06:40Z",
              "buildStageDurationSeconds": 390,
              "queueToArtifactReadySeconds": 400,
              "isOutlier": false,
              "isQueueOutlier": false,
              "excludedFromMetrics": false,
              "outlierReason": null,
              "tasks": [],
              "artifacts": []
            }
          ]
        }
      ]
    }
  ]
}
```

## Ejemplo CSV

`builds-summary.csv`

```csv
repositoryName,pipelineId,pipelineName,buildId,buildNumber,branch,status,result,queueTime,startTime,finishTime,queueDurationSeconds,buildDurationSeconds,queueToFinishDurationSeconds,buildStageStartTime,buildStageFinishTime,artifactReadyTime,buildStageDurationSeconds,queueToArtifactReadySeconds,isOutlier,isQueueOutlier,outlierReason,excludedFromMetrics,artifactTotalSizeMb
repo-a,15,repo-a-ci,1001,20260604.1,refs/heads/develop,completed,succeeded,2026-06-04T12:00:00.0000000Z,2026-06-04T12:00:12.0000000Z,2026-06-04T12:07:12.0000000Z,12,420,432,2026-06-04T12:00:15.0000000Z,2026-06-04T12:06:45.0000000Z,2026-06-04T12:06:40.0000000Z,390,400,false,false,,false,125.5
```
