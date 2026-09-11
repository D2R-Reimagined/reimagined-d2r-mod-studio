param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version,
    [ValidateSet('win-x64','linux-x64')][string] $Runtime = 'win-x64',
    [string] $Dotnet = 'dotnet',
    [string] $PackageCache = '',
    [string] $RepositoryUrl = 'https://github.com/D2R-Reimagined/reimagined-d2r-mod-studio'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "artifacts/release/$Version/$Runtime/publish"
$output = Join-Path $root "artifacts/release/$Version/$Runtime/velopack"
& "$PSScriptRoot/publish.ps1" -Runtime $Runtime -Dotnet $Dotnet -PackageCache $PackageCache -Version $Version -UpdateRepositoryUrl $RepositoryUrl -OutputDirectory $publish
# Install the GUI at package root; retain the CLI and all distribution notices.
foreach ($name in @('cli','docs','licenses','README.md','LICENSE','THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $publish $name) -Destination (Join-Path $publish 'app') -Recurse
}
[xml]$project = Get-Content (Join-Path $root 'src/ModStudio.App/ModStudio.App.csproj')
$toolVersion = $project.Project.PropertyGroup.VelopackVersion
$vpk = Join-Path $root "artifacts/tools/vpk-$toolVersion/vpk"
if (!(Test-Path $vpk) -and !(Test-Path "$vpk.exe")) {
    & $Dotnet tool install vpk --version $toolVersion --tool-path (Join-Path $root "artifacts/tools/vpk-$toolVersion") --configfile (Join-Path $root 'NuGet.Config') --allow-roll-forward
    if ($LASTEXITCODE -ne 0) { throw 'Velopack tool installation failed.' }
}
$env:DOTNET_ROOT = Split-Path -Parent (Get-Command $Dotnet).Source
$channel = if ($Runtime.StartsWith('win')) { 'win' } else { 'linux' }
$arguments = @()
if ($channel -eq 'linux' -and $IsWindows) { $arguments += '[linux]' }
$arguments += @('--yes','pack','--packId','D2RReimagined.ModStudio','--packVersion',$Version,
    '--packDir',(Join-Path $publish 'app'),'--mainExe',$(if ($channel -eq 'win') { 'ModStudio.App.exe' } else { 'ModStudio.App' }),
    '--packTitle','Reimagined D2R Mod Studio','--packAuthors','D2R Reimagined',
    '--runtime',$Runtime,'--channel',$channel,'--outputDir',$output,
    '--icon',(Join-Path $root ('src/ModStudio.App/Assets/ReimaginedModStudio.' + $(if ($channel -eq 'win') { 'ico' } else { 'png' }))))
if ($channel -eq 'linux') { $arguments += @('--categories','Game;Utility') }
& $vpk @arguments
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
Write-Host "Velopack release ready: $output"
