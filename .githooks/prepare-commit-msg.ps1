# Auto-prefix commit messages with the current app version from src/Furchive/Furchive.csproj
param(
    [string]$MessageFile,
    [string]$Source,
    [string]$Sha
)

# Resolve repo root (.githooks is directly under repo root)
$repoRoot = Split-Path -Parent $PSScriptRoot
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

# Debug log (non-fatal)
try {
    $logPath = Join-Path $repoRoot '.git/prepare-commit-msg.log'
    $stamp = Get-Date -Format o
    "[$stamp] file='$MessageFile' version='$version' len=$($msg.Length)" | Out-File -FilePath $logPath -Append -Encoding UTF8
} catch {}

# If already prefixed with the version, skip (escape for regex)
if ($msg -match ("^" + [regex]::Escape($version) + "[: ]")) { return }

# Add prefix "<version>: " ahead of the first non-comment line
$lines = @(Get-Content -LiteralPath $MessageFile)
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^(#|\s*$)') { continue }
    $lines[$i] = ($version + ": " + $line)
    break
}
$updated = $false
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^(#|\s*$)') { continue }
    $updated = $true
    break
}
if (-not $updated) {
    $prefixLine = ($version + ": ")
    $lines = @($prefixLine) + $lines
}
$lines | Set-Content -LiteralPath $MessageFile -Encoding UTF8
