param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $Runtime = 'win-x64',
    [string] $Dotnet = 'dotnet',
    [string] $PackageCache = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $projectRoot "artifacts/packages/$Runtime/$(Get-Date -Format 'yyyyMMdd-HHmmss')"
if (Test-Path -LiteralPath $output) { throw "Package destination already exists: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
$runtimeNoticeCount = 0
foreach ($project in @('ModStudio.App', 'ModStudio.Cli')) {
    $projectFile = Join-Path $projectRoot "src/$project/$project.csproj"
    $restoreArgs = @('restore', $projectFile, '-r', $Runtime, '--configfile', (Join-Path $projectRoot 'NuGet.Config'), '-p:SelfContained=true')
    if ($PackageCache) { $restoreArgs += @('--packages', $PackageCache) }
    & $Dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
    $appOutput = Join-Path $output $(if ($project -eq 'ModStudio.App') { 'app' } else { 'cli' })
    & $Dotnet publish $projectFile -c Release -r $Runtime --self-contained true --no-restore -o $appOutput -p:PublishTrimmed=false -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot "src/$project/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $assets.libraries.GetEnumerator()) {
        if ($entry.Value.type -ne 'package') { continue }
        foreach ($cache in $assets.packageFolders.Keys) {
            $package = Join-Path $cache $entry.Value.path
            if (!(Test-Path -LiteralPath $package)) { continue }
            foreach ($notice in (Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -match 'LICENSE|LICENCE|NOTICE|COPYING' })) {
                $noticeDest = Join-Path $output ('licenses/packages/' + $entry.Value.path + '/' + [IO.Path]::GetRelativePath($package, $notice.FullName))
                New-Item -ItemType Directory -Path (Split-Path -Parent $noticeDest) -Force | Out-Null
                Copy-Item -LiteralPath $notice.FullName -Destination $noticeDest -Force
            }
        }
    }
    foreach ($framework in $assets.project.frameworks.Values) {
        foreach ($dependency in $framework.downloadDependencies) {
            if ($dependency.name -notlike 'Microsoft.NETCore.App.Runtime.*') { continue }
            $version = ($dependency.version.Trim([char[]]'[]') -split ',')[0].Trim()
            foreach ($cache in $assets.packageFolders.Keys) {
                $runtimePackage = Join-Path $cache ($dependency.name.ToLowerInvariant() + '/' + $version)
                if (!(Test-Path -LiteralPath $runtimePackage)) { continue }
                foreach ($notice in (Get-ChildItem -LiteralPath $runtimePackage -File | Where-Object { $_.Name -match 'LICENSE|NOTICE' })) {
                    $runtimeNotices = Join-Path $output 'licenses/dotnet-runtime'
                    New-Item -ItemType Directory -Path $runtimeNotices -Force | Out-Null
                    Copy-Item -LiteralPath $notice.FullName -Destination $runtimeNotices -Force
                    $runtimeNoticeCount++
                }
            }
        }
    }
}
if ($runtimeNoticeCount -eq 0) { throw 'Self-contained runtime notices were not found; package is incomplete.' }
if ($Runtime.StartsWith('osx-')) {
    $bundle = Join-Path $output 'Reimagined D2R Mod Studio.app/Contents'
    New-Item -ItemType Directory -Path $bundle -Force | Out-Null
    Move-Item -LiteralPath (Join-Path $output 'app') -Destination (Join-Path $bundle 'MacOS')
    $plist = @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>ModStudio.App</string>
<key>CFBundleIdentifier</key><string>community.modstudio</string>
<key>CFBundleName</key><string>Reimagined D2R Mod Studio</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>0.2.0</string>
<key>CFBundleVersion</key><string>1</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
'@
    [IO.File]::WriteAllText((Join-Path $bundle 'Info.plist'), $plist)
}
foreach ($file in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $output
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $output -Recurse -Force
if ($Runtime.StartsWith('win-')) { Compress-Archive -Path (Join-Path $output '*') -DestinationPath "$output.zip" }
else {
    & tar -czf "$output.tar.gz" -C $output .
    if ($LASTEXITCODE -ne 0) { throw 'Archive failed' }
}
Write-Host "Package ready: $output"
