[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AssettoCorsaPath
)

$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $AssettoCorsaPath).Path
$gameExe = Join-Path $gameRoot 'acs.exe'
if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) {
    throw "Non trovo acs.exe in '$gameRoot'. Passa la cartella principale di Assetto Corsa."
}

$bridgeSource = Join-Path $PSScriptRoot '..\ac_bridge\AiRaceEngineer.py'
if (-not (Test-Path -LiteralPath $bridgeSource -PathType Leaf)) {
    throw "Manca il file del ponte AC: $bridgeSource"
}

$targetDirectory = Join-Path $gameRoot 'apps\python\AiRaceEngineer'
New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
Copy-Item -LiteralPath $bridgeSource -Destination (Join-Path $targetDirectory 'AiRaceEngineer.py') -Force

$documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
$configDirectory = Join-Path $documents 'Assetto Corsa\cfg'
New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
$pythonIni = Join-Path $configDirectory 'python.ini'
$content = if (Test-Path -LiteralPath $pythonIni) { [System.IO.File]::ReadAllText($pythonIni) } else { '' }

$sectionPattern = '(?im)^\[AIRACEENGINEER\][^\r\n]*'
$sectionMatch = [regex]::Match($content, $sectionPattern)
if ($sectionMatch.Success) {
    $sectionStart = $sectionMatch.Index
    $afterHeader = $sectionMatch.Index + $sectionMatch.Length
    $nextSection = [regex]::Match($content.Substring($afterHeader), '(?m)^\[[^\]\r\n]+\][^\r\n]*')
    $sectionEnd = if ($nextSection.Success) { $afterHeader + $nextSection.Index } else { $content.Length }
    $sectionBody = $content.Substring($afterHeader, $sectionEnd - $afterHeader)
    if ([regex]::IsMatch($sectionBody, '(?im)^\s*ACTIVE\s*=')) {
        $sectionBody = [regex]::Replace($sectionBody, '(?im)^\s*ACTIVE\s*=\s*[^\r\n]*', 'ACTIVE=1')
    } else {
        $sectionBody = "`r`nACTIVE=1" + $sectionBody
    }
    $content = $content.Substring(0, $afterHeader) + $sectionBody + $content.Substring($sectionEnd)
} else {
    if ($content.Length -gt 0 -and -not $content.EndsWith("`n")) { $content += "`r`n" }
    $content += "`r`n[AIRACEENGINEER]`r`nACTIVE=1`r`n"
}

if (Test-Path -LiteralPath $pythonIni) {
    $backup = "$pythonIni.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $pythonIni -Destination $backup
    Write-Host "Configurazione salvata in: $backup"
}
[System.IO.File]::WriteAllText($pythonIni, $content, [System.Text.UTF8Encoding]::new($false))

Write-Host "Ponte installato: $(Join-Path $targetDirectory 'AiRaceEngineer.py')"
Write-Host "App attivata in: $pythonIni"
Write-Host 'Riavvia Assetto Corsa e apri AiRaceEngineer dal selettore app in pista.'
