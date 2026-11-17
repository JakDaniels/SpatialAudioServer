# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Builds the Windows installers (PACKAGING-PLAN.md "Windows installers"): the server and the load tool as MSIs, each
# with its own GStreamer, into dist\.
#   scripts\build-windows.ps1 -GStreamerRoot <folder of an installed GStreamer 1.28 (MSVC, x64)> -GStreamerVersion 1.28.8
# Needs the .NET SDK, Python with pefile (pip install pefile) and a git clone with its history (MinVer). WiX comes
# from NuGet through the .wixproj files. Used by .github/workflows/drafts.yml.
param(
    [Parameter(Mandatory = $true)][string]$GStreamerRoot,
    [Parameter(Mandatory = $true)][string]$GStreamerVersion
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root
function Run { param([string]$exe, [string[]]$arguments) & $exe @arguments; if ($LASTEXITCODE -ne 0) { throw "$exe failed ($LASTEXITCODE)" } }

# Versions: SemVer from MinVer for the file names; MSI wants numbers only (major.minor.patch.build), so a pre-release
# 0.2.1-alpha.0.5 becomes 0.2.1.5 (MSI compares the first three parts; the installers allow same-version upgrades).
$version = (& dotnet msbuild src/SpatialAudioServer -restore -t:MinVer -getProperty:MinVerVersion -nologo | Select-Object -Last 1).Trim()
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(-.*\.(\d+))?$') { throw "Unexpected version '$version'" }
$build = if ($Matches[5]) { $Matches[5] } else { '0' }
$msiVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$build"
Write-Host "Version $version (MSI $msiVersion)"

$stage = Join-Path $root 'dist\stage-win'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage, (Join-Path $root 'dist') | Out-Null

$publish = @('-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:DebugType=none')
Run dotnet (@('publish', 'src/SpatialAudioServer') + $publish + @('-o', "$stage\server"))
Run dotnet (@('publish', 'src/SpatialAudioServer.LoadTest') + $publish + @('-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true', '-o', "$stage\loadtest"))
# A developer's own settings (may hold test API keys) never go into an installer.
Remove-Item -ErrorAction SilentlyContinue "$stage\server\appsettings.Development.json"
Copy-Item -Recurse scripts\loadtest-linux-x64\voices "$stage\voices"

Run python @('packaging\windows\gather-gstreamer.py', $GStreamerRoot, "$stage\gstreamer")

$notices = @"
Third-party software in this installer
======================================

GStreamer $GStreamerVersion (gstreamer folder): the official MSVC x86_64 build from
https://gstreamer.freedesktop.org/data/pkg/windows/$GStreamerVersion/msvc/, of which only the
libraries and plugins this program needs are included. GStreamer is licensed under the GNU Lesser General
Public License 2.1 or later; the libraries it uses have their own open-source licences (texts in
gstreamer\share\licenses). Source code: https://gitlab.freedesktop.org/gstreamer/gstreamer/-/tree/$GStreamerVersion
and https://gstreamer.freedesktop.org/src/. They are separate libraries, loaded at run time, and may be replaced
by compatible builds.

.NET runtime and ASP.NET Core (included in the program): MIT licence, https://github.com/dotnet/runtime and
https://github.com/dotnet/aspnetcore.
"@
foreach ($docs in @("$stage\docs", "$stage\loadtest-docs")) { New-Item -ItemType Directory -Force -Path $docs | Out-Null }
Copy-Item README.md, docs\running.md, docs\internet.md "$stage\docs"
Copy-Item scripts\loadtest-linux-x64\README.md "$stage\loadtest-docs"
# The release notes so far, from the tags' messages, as scripts/changelog.sh writes them (bash is not dependable here).
$changelog = @('# Changelog', '', 'What changed in each release: the same text as its release notes.', '')
& git describe --exact-match --tags --match 'v[0-9]*' HEAD 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    # The "## Unreleased" entries of CHANGELOG.md.
    $inside = $false
    $unreleased = foreach ($line in (Get-Content -ErrorAction SilentlyContinue CHANGELOG.md)) {
        if ($line -match '^## ') { $inside = $line -match '^## Unreleased'; continue }
        if ($inside) { $line }
    }
    $text = ($unreleased -join "`n").Trim()
    if (-not $text) { $text = 'Nothing noted since the last release.' }
    $changelog += @("## Unreleased ($version)", '', $text, '')
}
foreach ($tag in (& git -c versionsort.suffix=- for-each-ref --sort=-v:refname --format='%(refname:strip=2)' 'refs/tags/v[0-9]*')) {
    $date = (& git tag -l --format='%(taggerdate:short)' $tag) -join ''
    if (-not $date) { $date = (& git log -1 --format=%cs $tag) -join '' }
    $message = ((& git tag -l --format='%(contents)' $tag) -join "`n") -replace '(?s)-----BEGIN PGP SIGNATURE-----.*$', ''
    $changelog += @("## $($tag.Substring(1)) ($date)", '', $message.Trim(), '')
}
$global:LASTEXITCODE = 0
foreach ($docs in @("$stage\docs", "$stage\loadtest-docs")) {
    [IO.File]::WriteAllText("$docs\CHANGELOG.md", ($changelog -join "`n") + "`n")
    Copy-Item LICENSE "$docs\LICENSE.txt"
    Copy-Item NOTICE "$docs\NOTICE.txt"
    Set-Content -Path "$docs\THIRD-PARTY-NOTICES.txt" -Value $notices -Encoding utf8
}

foreach ($msi in @(@{ Project = 'server\Server.wixproj'; Name = 'SpatialAudioServer' },
                   @{ Project = 'loadtest\LoadTest.wixproj'; Name = 'SpatialAudioServer-LoadTest' })) {
    Run dotnet @('build', "packaging\windows\$($msi.Project)", '-c', 'Release', "-p:ProductVersion=$msiVersion",
        "-p:Stage=$stage", '-o', "$stage\msi")
    Copy-Item "$stage\msi\$($msi.Name).msi" "dist\$($msi.Name)-$version-x64.msi"
}
Get-ChildItem dist\*.msi | Format-Table Name, Length
