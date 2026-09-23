param(
    [string]$Configuration = 'Release',
    [string]$Version = '0.6.1.0',
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\outputs')
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'verify.ps1') -Configuration $Configuration -Dotnet $Dotnet

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dll = Join-Path $repositoryRoot "src\Jellyfin.Plugin.LocalRating\bin\$Configuration\net10.0\Jellyfin.Plugin.LocalRating.dll"
$readme = Join-Path $repositoryRoot 'README.md'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$artifact = Join-Path $outputRoot "LocalRating_$Version.zip"
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("jellyfin-localrating-package-" + [Guid]::NewGuid().ToString('N'))
$pluginFolder = Join-Path $stagingRoot "Local Rating_$Version"

try {
    New-Item -ItemType Directory -Path $pluginFolder -Force | Out-Null
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    Copy-Item -LiteralPath $dll -Destination $pluginFolder
    Copy-Item -LiteralPath $readme -Destination $pluginFolder
    Compress-Archive -LiteralPath $pluginFolder -DestinationPath $artifact -Force
    $hash = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
    Write-Host "PACKAGE_OK: $artifact"
    Write-Host "SHA256: $($hash.Hash)"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedStaging.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Package staging path is outside the temporary directory.'
        }
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
