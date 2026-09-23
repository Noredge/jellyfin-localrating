param(
    [string]$Configuration = 'Release',
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '..\LocalRating.slnx'
$testProject = Join-Path $PSScriptRoot '..\tests\Jellyfin.Plugin.LocalRating.Tests\Jellyfin.Plugin.LocalRating.Tests.csproj'
$nugetConfig = Join-Path $PSScriptRoot '..\NuGet.Config'
$restoreArguments = @('restore', $solution, '--configfile', $nugetConfig)
if ($env:CI -eq 'true') {
    $restoreArguments += '--locked-mode'
}

& $Dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
& $Dotnet build $solution --configuration $Configuration --no-restore --no-incremental
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
& $Dotnet format $solution --verify-no-changes --no-restore --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Formatting verification failed with exit code $LASTEXITCODE." }
& $Dotnet test --project $testProject --configuration $Configuration --no-restore --no-build --minimum-expected-tests 69
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }

$dll = Join-Path $PSScriptRoot "..\src\Jellyfin.Plugin.LocalRating\bin\$Configuration\net10.0\Jellyfin.Plugin.LocalRating.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "Missing plugin assembly: $dll" }

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $dll))
$resources = $assembly.GetManifestResourceNames()
foreach ($required in @(
    'Jellyfin.Plugin.LocalRating.Web.localrating.css',
    'Jellyfin.Plugin.LocalRating.Web.localrating.js',
    'Jellyfin.Plugin.LocalRating.Web.configuration.html'
)) {
    if ($resources -notcontains $required) { throw "Missing embedded resource: $required" }
}

# Jellyfin loads shared assemblies from the server process. A plugin compiled
# against a different patch version can build and test successfully, but fail
# before startup when Jellyfin cannot satisfy the exact assembly reference.
$requiredSharedAssemblyVersions = @{
    'Microsoft.Data.Sqlite' = [Version]'10.0.11.0'
    'MediaBrowser.Common' = [Version]'12.0.0.0'
    'MediaBrowser.Controller' = [Version]'12.0.0.0'
    'MediaBrowser.Model' = [Version]'12.0.0.0'
}
$assemblyReferences = $assembly.GetReferencedAssemblies()
foreach ($requiredReference in $requiredSharedAssemblyVersions.GetEnumerator()) {
    $actualReference = $assemblyReferences | Where-Object Name -EQ $requiredReference.Key
    if (-not $actualReference) {
        throw "Missing shared assembly reference: $($requiredReference.Key)"
    }

    if ($actualReference.Version -ne $requiredReference.Value) {
        throw "Shared assembly version mismatch for $($requiredReference.Key): expected $($requiredReference.Value), got $($actualReference.Version)."
    }
}

$webFiles = Get-ChildItem (Join-Path $PSScriptRoot '..\src\Jellyfin.Plugin.LocalRating\Web') -File
$externalReference = $webFiles | Select-String -Pattern 'https?://'
if ($externalReference) { throw 'Web assets contain an external HTTP reference.' }

Write-Host "VERIFICATION_OK: $dll"
