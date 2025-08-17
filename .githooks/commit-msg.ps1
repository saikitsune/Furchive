# Auto-prefix commit messages with the current app version from src/Furchive/Furchive.csproj
param(
    [Parameter(Mandatory=$true)][string]$MessageFile
)

# Resolve repo root (.githooks is directly under repo root)
$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repoRoot 'src\Furchive\Furchive.csproj'
if (-not (Test-Path $csproj)) { return }

[xml]$xml = Get-Content $csproj
$versionNodes = @(
    $xml.Project.PropertyGroup.AssemblyVersion,
    $xml.Project.PropertyGroup.FileVersion,
    $xml.Project.PropertyGroup.Version
) | Where-Object { $_ -and $_ -ne '' }
$version = $versionNodes[0]
if (-not $version) { return }

if (-not (Test-Path $MessageFile)) { return }
$msg = Get-Content -Raw -LiteralPath $MessageFile

# Debug log (non-fatal)
try {
    $logPath = Join-Path $repoRoot '.git/commit-msg.log'
    $stamp = Get-Date -Format o
    "[$stamp] file='$MessageFile' version='$version' len=$($msg.Length)" | Out-File -FilePath $logPath -Append -Encoding UTF8
} catch {}
if ($msg -match "^\Q$version\E[: ]") { return }

$lines = Get-Content -LiteralPath $MessageFile
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^(#|\s*$)') { continue }
    $lines[$i] = "$(($version)): $line"
    break
}
$lines | Set-Content -LiteralPath $MessageFile -Encoding UTF8
