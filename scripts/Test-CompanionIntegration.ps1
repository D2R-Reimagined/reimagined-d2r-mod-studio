param(
    [Parameter(Mandatory)][string]$StudioExecutable,
    [Parameter(Mandatory)][string]$LevelEditorExecutable,
    [Parameter(Mandatory)][string]$GameData,
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/companion-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new smoke directory.' }
$studio = (Resolve-Path -LiteralPath $StudioExecutable).Path
$level = (Resolve-Path -LiteralPath $LevelEditorExecutable).Path
$data = (Resolve-Path -LiteralPath $GameData).Path
$fixture = Join-Path $output 'project'
$shared = Join-Path $repo 'src/ModStudio.Core/Companion/EditorIntegration.cs'
$sibling = Join-Path (Split-Path $repo -Parent) 'd2r-level-editor/src/D2RLevel.Core/Companion/EditorIntegration.cs'
if ((Test-Path -LiteralPath $sibling) -and (Get-FileHash $shared).Hash -ne (Get-FileHash $sibling).Hash) { throw 'Companion protocol source copies differ.' }
& $Dotnet run --project (Join-Path $repo 'tests/ModStudio.Tests') -c Release --no-build -- --create-ui-fixture $fixture
if ($LASTEXITCODE) { throw 'Fixture creation failed.' }
$previousRegistry = $env:REIMAGINED_INTEGRATION_HOME
$previousPreferences = $env:MOD_STUDIO_PREFERENCES
try {
    $env:REIMAGINED_INTEGRATION_HOME = Join-Path $output 'registry'
    $env:MOD_STUDIO_PREFERENCES = Join-Path $output 'studio-preferences.json'
    $start = [Diagnostics.ProcessStartInfo]::new($studio)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($arg in @('--smoke', $fixture, $output, '--companion-only', '--level-editor-exe', $level, '--game-data', $data)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $output 'companion-smoke.txt'))) {
        if (Test-Path -LiteralPath (Join-Path $output 'failure.txt')) { Get-Content (Join-Path $output 'failure.txt') }
        throw "Companion smoke failed. Inspect $output"
    }
    Get-Content (Join-Path $output 'companion-smoke.txt')
    Write-Output "Screenshots and fixture: $output"
}
finally {
    $env:REIMAGINED_INTEGRATION_HOME = $previousRegistry
    $env:MOD_STUDIO_PREFERENCES = $previousPreferences
}
