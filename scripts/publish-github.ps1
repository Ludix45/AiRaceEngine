[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(https://github\.com/|git@github\.com:)')]
    [string]$Remote
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$inside = git -C $repo rev-parse --is-inside-work-tree 2>$null
if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') {
    throw "La cartella non è una repository Git: $repo"
}

$existing = git -C $repo remote get-url origin 2>$null
if ($LASTEXITCODE -eq 0 -and $existing) {
    if ($existing.TrimEnd('/') -ne $Remote.TrimEnd('/')) {
        throw "Esiste già origin ($existing). Rimuovilo o correggilo manualmente prima di pubblicare."
    }
} else {
    git -C $repo remote add origin $Remote
    if ($LASTEXITCODE -ne 0) { throw 'Non sono riuscito ad aggiungere il remote origin.' }
}

git -C $repo push -u origin main
if ($LASTEXITCODE -ne 0) { throw 'Push non riuscito. Controlla URL e autenticazione GitHub.' }
Write-Host 'Repository pubblicata. I push successivi su main avviano la build Windows.'
