[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\publish\win-x64')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\AiRaceEngineer.csproj'
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)

dotnet publish $project -c Release -r win-x64 --self-contained true -o $resolvedOutput
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish non riuscito (codice $LASTEXITCODE)."
}

Write-Host "Build pronta: $resolvedOutput"
