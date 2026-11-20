[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

git -C $repo config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    throw 'Non sono riuscito a impostare core.hooksPath. Controlla che la cartella sia una repository Git.'
}

git -C $repo update-index --chmod=+x .githooks/pre-commit 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Nota: impossibile registrare il permesso di esecuzione (normale su Windows).'
}

Write-Host "Hook installati: core.hooksPath = .githooks"
Write-Host 'Da ora ogni commit viene controllato e bloccato se aggiunge chiavi API, token o chiavi private.'
