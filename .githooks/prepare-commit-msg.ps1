# Auto-prefix commit messages with the current app version from src/Furchive/Furchive.csproj
param(
    [string]$MessageFile,
    [string]$Source,
    [string]$Sha
)

# Resolve repo root
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csproj = Join-Path $repoRoot 'src\Furchive\Furchive.csproj'
if (-not (Test-Path $csproj)) { return }

# Extract version (prefer AssemblyVersion or Version)
[xml]$xml = Get-Content $csproj
$versionNodes = @(
    $xml.Project.PropertyGroup.AssemblyVersion,
    $xml.Project.PropertyGroup.FileVersion,
    $xml.Project.PropertyGroup.Version
) | Where-Object { $_ -and $_ -ne '' }
$version = $versionNodes[0]
if (-not $version) { return }

# Read existing message
if (-not (Test-Path $MessageFile)) { return }
$msg = Get-Content -Raw -LiteralPath $MessageFile

# If already prefixed with the version, skip
if ($msg -match "^\Q$version\E[: ]") { return }

# Add prefix "<version>: " ahead of the first non-comment line
$lines = Get-Content -LiteralPath $MessageFile
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^(#|\s*$)') { continue }
    $lines[$i] = "$(($version)): $line"
    break
}
$lines | Set-Content -LiteralPath $MessageFile -Encoding UTF8
