
# Default values for parameters
if (-not $Configuration) { $Configuration = "Release" }
if (-not $Project) { $Project = "src/Furchive.Avalonia/Furchive.Avalonia.csproj" }

$ErrorActionPreference = 'Stop'

<#
  Enhanced publish script:
  - Performs an explicit dotnet clean (and manual bin/obj removal) once up front to eliminate stale Avalonia compiled XAML causing phantom AVLN:0004 errors.
  - For each RID, ensures the output folder is freshly recreated.
  - Emits a diagnostic summary per RID on success.
  - Leaves SingleFile enabled (toggle by changing singleFile value below for quick A/B tests).
  - To debug the "silent exe" issue you can temporarily set singleFile=$false for win-x64 and compare behavior.
#>

$targets = @(
    @{ rid = 'win-x64';   output = 'publish/win-x64';   singleFile = $true },
    @{ rid = 'osx-x64';   output = 'publish/osx-x64';   singleFile = $true },
    @{ rid = 'osx-arm64'; output = 'publish/osx-arm64'; singleFile = $true },
    @{ rid = 'linux-x64'; output = 'publish/linux-x64'; singleFile = $true }
)

Write-Host "Performing full clean to purge stale compiled XAML (bin/obj)..."
try {
    dotnet clean "$Project" -c $Configuration | Out-Null
} catch {
    Write-Warning "dotnet clean failed: $($_.Exception.Message)"
}
Get-ChildItem -Path (Split-Path $Project -Parent) -Directory -Filter bin -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
Get-ChildItem -Path (Split-Path $Project -Parent) -Directory -Filter obj -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "Clean complete. Starting publishes..."

foreach ($t in $targets) {
    $outDir = Join-Path (Split-Path $Project -Parent) $t.output
    if (Test-Path $outDir) {
        Write-Host "Cleaning $outDir..."
        Remove-Item $outDir -Recurse -Force
    }
    Write-Host "Publishing for $($t.rid) to $outDir..."
    $publishCmd = "dotnet publish `"$Project`" -c $Configuration -r $($t.rid) --self-contained true /p:PublishSingleFile=$($t.singleFile) /p:PublishTrimmed=false -o `"$outDir`""
    Write-Host $publishCmd
    Invoke-Expression $publishCmd
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed for $($t.rid)."
        exit $LASTEXITCODE
    }
    else {
        Write-Host "RID $($t.rid) publish succeeded. Contents:"
        Get-ChildItem $outDir | Select-Object Name,Length | Format-Table -AutoSize | Out-String | Write-Host
    }
}

Write-Host "Publish complete. Outputs under src/Furchive.Avalonia/publish/*"
Write-Host "(Installer launch suppressed during debug - run installer script separately if needed.)"

# NOTE: Ensure only ASCII characters are used in this script. Non-ASCII (en dash, smart quotes) previously
# caused parser errors when invoked via some task runners. The message above intentionally uses a plain hyphen.