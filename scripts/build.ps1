[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\publish\win-x64'),
    [string]$Version = ""
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\AiRaceEngineer.csproj'
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)

$publishArguments = @($project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $resolvedOutput)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $numericVersion = ($Version -split '-', 2)[0]
    if ($numericVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Versione non valida: $Version. Usa X.Y.Z oppure X.Y.Z-suffisso."
    }
    $assemblyVersion = "$numericVersion.0"
    $publishArguments += "/p:Version=$Version"
    $publishArguments += "/p:AssemblyVersion=$assemblyVersion"
    $publishArguments += "/p:FileVersion=$assemblyVersion"
    $publishArguments += "/p:InformationalVersion=$Version"
}
dotnet publish @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish non riuscito (codice $LASTEXITCODE)."
}

Write-Host "Build pronta: $resolvedOutput"
