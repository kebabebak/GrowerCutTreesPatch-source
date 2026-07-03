$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$required = @(
    'libs\WorkTab.dll'
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $root $path))) {
        Write-Host "Missing: $path" -ForegroundColor Red
        Write-Host "See libs\README.txt for which files to copy." -ForegroundColor Yellow
        exit 1
    }
}

Write-Host 'Building GrowerCutTreesPatch (Release)...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'GrowerCutTreesPatch.csproj') -c Release

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host "Output: $(Join-Path $root 'out\GrowerCutTreesPatch.dll')" -ForegroundColor Green
