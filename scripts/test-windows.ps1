# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Installs the Windows MSIs on this (clean, administrator) machine and checks they work: the service starts as its
# virtual account with the embedded GStreamer (no other GStreamer may be found: run it after removing the one the
# build used), the apikey command reaches the service's database, every GStreamer element is there, the load tool
# passes a short run against the service, and both uninstall cleanly. Used by .github/workflows/drafts.yml.
#   scripts\test-windows.ps1 -Dist dist
param([string]$Dist = 'dist')
$ErrorActionPreference = 'Stop'
function Msi { param([string[]]$arguments, [string]$log)
    $p = Start-Process msiexec.exe -ArgumentList ($arguments + @('/qn', '/norestart', '/l*v', $log)) -Wait -PassThru
    if ($p.ExitCode -ne 0) { Get-Content $log -Tail 60; throw "msiexec $($arguments -join ' ') failed ($($p.ExitCode))" }
}
$server = (Get-ChildItem "$Dist\SpatialAudioServer-[0-9]*-x64.msi").FullName
$loadtest = (Get-ChildItem "$Dist\SpatialAudioServer-LoadTest-*-x64.msi").FullName
$program = "$env:ProgramFiles\SpatialAudioServer\SpatialAudioServer.exe"

Write-Host '== install the server (with the firewall rules)'
Msi @('/i', $server, 'ADDLOCAL=ALL') "$env:RUNNER_TEMP\server-install.log"
Get-Service SpatialAudioServer | Format-List Name, Status, StartType
(Get-CimInstance Win32_Service -Filter "Name='SpatialAudioServer'").StartName
Get-NetFirewallRule -DisplayName 'SpatialAudioServer*' | Format-Table DisplayName, Enabled, Direction
& $program --version

Write-Host '== an API key (as an administrator, into the service''s database)'
$key = (& $program apikey create ci | Select-String '^sas_').Line
if (-not $key) { throw 'No API key created' }
Restart-Service SpatialAudioServer # not needed for keys; checks that it restarts

$status = $null
foreach ($i in 1..40) {
    try { $status = Invoke-RestMethod -Uri http://localhost:5000/api/status -Headers @{ 'X-Api-Key' = $key }; break }
    catch { Start-Sleep -Seconds 1 }
}
if (-not $status) {
    Get-WinEvent -LogName Application -MaxEvents 30 | Where-Object ProviderName -like '*SpatialAudio*' | Format-List TimeCreated, Message
    throw 'The service did not answer'
}
$status | ConvertTo-Json -Depth 4
if (-not $status.gstreamer.ok -or $status.gstreamer.missingElements.Count -gt 0) { throw 'GStreamer is not complete' }
# The acousticmultimixer plugin the installer carries: the acoustic engine is offered only when it loads.
if ($status.engines.name -notcontains 'acoustic') { throw 'The acoustic engine is not offered (acousticmultimixer plugin missing?)' }
Get-ChildItem "$env:ProgramData\SpatialAudioServer", "$env:ProgramData\SpatialAudioServer\data"

Write-Host '== the load tool'
Msi @('/i', $loadtest) "$env:RUNNER_TEMP\loadtest-install.log"
$tool = "${env:ProgramFiles}\SpatialAudioServer Load Test\SpatialAudioServer.LoadTest.exe"
& $tool --version
$out = & $tool --server http://localhost:5000 --api-key $key --participants 2 --duration 5 --warmup 3 2>&1
$out
if (-not ($out -match 'RESULT: PASS')) { throw 'Load run failed' }

Write-Host '== uninstall'
Msi @('/x', $loadtest) "$env:RUNNER_TEMP\loadtest-uninstall.log"
Msi @('/x', $server) "$env:RUNNER_TEMP\server-uninstall.log"
if (Get-Service SpatialAudioServer -ErrorAction SilentlyContinue) { throw 'The service is still there' }
if (-not (Test-Path "$env:ProgramData\SpatialAudioServer\data\spatialaudio.db")) { throw 'The data was not kept' }
if (Test-Path "$env:ProgramFiles\SpatialAudioServer") { Get-ChildItem -Recurse "$env:ProgramFiles\SpatialAudioServer" | Select-Object -First 20 }
Write-Host 'PASS'
