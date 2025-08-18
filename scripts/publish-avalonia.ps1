param(
    [string]$Configuration = "Release",
    [string]$Project = "src/Furchive.Avalonia/Furchive.Avalonia.csproj"
)

$ErrorActionPreference = 'Stop'

$targets = @(
    @{ rid = 'win-x64';   output = 'publish/win-x64';   singleFile = $false },
    @{ rid = 'osx-x64';   output = 'publish/osx-x64';   singleFile = $false },
    @{ rid = 'osx-arm64'; output = 'publish/osx-arm64'; singleFile = $false },
    @{ rid = 'linux-x64'; output = 'publish/linux-x64'; singleFile = $false }
)

foreach ($t in $targets) {
    $outDir = Join-Path (Split-Path $Project -Parent) $t.output
    dotnet publish $Project -c $Configuration -r $t.rid --self-contained true /p:PublishSingleFile=$($t.singleFile) /p:PublishTrimmed=false -o $outDir
}

Write-Host "Publish complete. Outputs under src/Furchive.Avalonia/publish/*"
