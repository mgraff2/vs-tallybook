# Regression guard for inter-mod compatibility. Boots a headless Vintage Story server for
# each mod combination and fails on any [Error]/[Warning] in the server log or any missing/
# unexpected marker.
#
# Tallybook is client-side only, so what a dedicated-server boot proves is narrower than for
# a universal mod — but still real: the server unpacks the zip, loads Tallybook.dll and
# instantiates its ModSystems (visible in server-debug.log) before ShouldLoad gates them off.
# That catches a broken zip, a bad modinfo/dependency declaration, an assembly that no longer
# loads against the target game version, and any accidental loss of the client-only gate.
# What it can NOT catch is client-side behaviour — recipe registry reads, inventory events,
# the HUD, the management dialog, handbook pin integration. Those stay on the manual
# checklist in README.md.
#
# Invariants enforced per combo:
#   - server reaches "Dedicated Server now running"
#   - zero [Error]/[Warning] lines in server-main.log
#   - tallybook and every expected companion modid appear in the "Mods, sorted by
#     dependency:" line, and the "Found N mods (0 disabled)" count is exact
#   - server-debug.log shows our assembly loaded and mod systems instantiated
#   - total server-side silence: exactly ONE tallybook mention in server-main.log (the
#     dependency-sort line). A second mention means the mod started logging/running on the
#     server — e.g. the Client side gate was lost — and fails the combo.
#
#   .\tools\compat-test.ps1              -> builds the zip, runs the full matrix
#   .\tools\compat-test.ps1 -SkipBuild   -> reuse the already-packaged zip
#   .\tools\compat-test.ps1 -ServerExe <path>\VintagestoryServer.exe
#                                        -> test against a different game version. Prefer
#                                           .\tools\version-sweep.ps1, which does 1.22.0
#                                           through 1.22.6 automatically.
#
# Companion mod zips are cached in tools\compat-cache\ (gitignored): first found in the live
# Mods folder, otherwise downloaded from the mod DB API (latest release for that mod).
# Delete the cache to re-source (e.g. after updating your live mods).
param(
    [switch]$SkipBuild,
    [string]$ServerExe = "$env:APPDATA\Vintagestory\VintagestoryServer.exe",
    [int]$BootTimeoutSec = 180
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$cache = "$PSScriptRoot\compat-cache"
New-Item -ItemType Directory -Force $cache | Out-Null

if (-not (Test-Path $ServerExe)) { throw "Server exe not found: $ServerExe" }

$version = (Get-Content "$root\Tallybook\modinfo.json" -Raw | ConvertFrom-Json).version
$ourZip = "$root\dist\tallybook_$version.zip"

if (-not $SkipBuild) {
    # System dotnet is SDK 9 and refuses the net10.0 game references; prefer the user-scoped SDK.
    $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
    & $dotnet build "$root\Tallybook\Tallybook.csproj" -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $staging = "$env:TEMP\tallybook-pack"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force $staging | Out-Null
    Copy-Item "$root\Tallybook\modinfo.json" $staging
    Copy-Item "$root\Tallybook\bin\Release\Tallybook.dll" $staging
    New-Item -ItemType Directory -Force "$root\dist" | Out-Null
    Compress-Archive -Path "$staging\*" -DestinationPath $ourZip -Force
}
if (-not (Test-Path $ourZip)) { throw "Mod zip not found: $ourZip" }

# Fetch a companion mod zip: cache -> live Mods folder -> mod DB API (latest release)
function Get-CompatMod([string]$modid, [string]$filePattern) {
    $cached = Get-ChildItem $cache -Filter $filePattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cached) { return $cached.FullName }

    $live = Get-ChildItem "$env:APPDATA\VintagestoryData\Mods" -Filter $filePattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($live) { Copy-Item $live.FullName $cache; return "$cache\$($live.Name)" }

    Write-Host "  downloading $modid from mod DB..."
    $info = Invoke-RestMethod "https://mods.vintagestory.at/api/mod/$modid"
    $release = $info.mod.releases | Select-Object -First 1
    $dest = "$cache\$($release.filename ?? "$modid.zip")"
    if (-not $dest.EndsWith(".zip")) { $dest = "$cache\$modid-$($release.modversion).zip" }
    Invoke-WebRequest ($release.mainfile -replace ' ', '%20') -OutFile $dest
    return $dest
}

# Companion set. Keep it derived from Tallybook's real interaction surface — hotkeys, HUD
# corners, GUI dialogs, per-world ModData files — not copied wholesale from another project.
# Add here as the surface grows (recipe-adding content mods once §8 registry reads land;
# HUD-corner mods once §5 ships), e.g.:
#   somemodid = Get-CompatMod "somemodid" "somemodid_*.zip"
Write-Host "Collecting companion mods..."
$mods = [ordered]@{}
$mods.GetEnumerator() | ForEach-Object { Write-Host "  $($_.Key): $(Split-Path $_.Value -Leaf)" }

# combos: solo, +each companion, all together. 'expect' = companion modids that must show
# up in the dependency-sort line alongside tallybook.
$combos = @(
    @{ name = "solo"; expect = @() }
)
foreach ($id in $mods.Keys) { $combos += @{ name = $id; expect = @($id) } }
if ($mods.Count -gt 1) { $combos += @{ name = "all"; expect = @($mods.Keys) } }

$results = @()
foreach ($combo in $combos) {
    $name = $combo.name
    Write-Host "== combo '$name' ..." -NoNewline
    # No "tallybook" in the dir name: the server logs the Mods search path into
    # server-main.log, which would trip the exactly-one-mention silence check below.
    #
    # The PID keeps concurrent runs apart. version-sweep.ps1 invokes this script repeatedly,
    # and a second run started by hand while a sweep is going would otherwise delete the
    # sweep's data directory mid-boot — producing a phantom "server did not start" failure
    # against a mod that is perfectly fine.
    $dp = "$env:TEMP\tbk-compat-$PID-$name"
    if (Test-Path $dp) { Remove-Item -Recurse -Force $dp }
    New-Item -ItemType Directory -Force "$dp\Mods" | Out-Null
    Copy-Item $ourZip "$dp\Mods"
    foreach ($id in $combo.expect) { Copy-Item $mods[$id] "$dp\Mods" }

    $proc = Start-Process $ServerExe -ArgumentList "--dataPath", $dp -PassThru -WindowStyle Hidden
    $log = "$dp\Logs\server-main.log"
    $debugLog = "$dp\Logs\server-debug.log"
    $booted = $false
    $deadline = (Get-Date).AddSeconds($BootTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep 2
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern "Dedicated Server now running" -Quiet)) { $booted = $true; break }
    }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep 1   # let file handles close before we read/delete

    $problems = @()
    if (-not $booted) { $problems += "server did not reach 'Dedicated Server now running' within ${BootTimeoutSec}s" }
    if (Test-Path $log) {
        $noise = Select-String -Path $log -Pattern "\[Error\]|\[Warning\]" | ForEach-Object Line
        if ($noise) { $problems += $noise }

        # base game contributes 3 mods (game, creative, survival); + ours + companions
        $expectedCount = 4 + $combo.expect.Count
        if (-not (Select-String -Path $log -SimpleMatch "Found $expectedCount mods (0 disabled)" -Quiet)) {
            $found = (Select-String -Path $log -Pattern "Found \d+ mods" | Select-Object -First 1).Line
            $problems += "expected 'Found $expectedCount mods (0 disabled)', got: $found"
        }

        $sortLine = (Select-String -Path $log -SimpleMatch "Mods, sorted by dependency:" | Select-Object -First 1).Line
        if (-not $sortLine) { $problems += "no 'Mods, sorted by dependency:' line" }
        foreach ($id in (@("tallybook") + $combo.expect)) {
            if ($sortLine -notmatch "[ ,]$id(,|`$| )") { $problems += "modid '$id' missing from load order: $sortLine" }
        }

        # Server-side silence: the sort line must be the ONLY tallybook mention in the main
        # log. Path echoes don't count as mod output — the server logs its Mods search path
        # and asset origins, and both sit under a directory literally named "vs-tallybook"
        # when the server package is cached in-repo. Blank the two known paths out of each
        # line before matching, so the check tests what the mod said, not where it lives.
        $serverRoot = Split-Path $ServerExe -Parent
        $mentions = @(Get-Content $log | Where-Object {
            ($_ -replace [regex]::Escape($serverRoot), "" -replace [regex]::Escape($dp), "") -match "tallybook"
        } | ForEach-Object { [pscustomobject]@{ Line = $_ } })
        if ($mentions.Count -ne 1) {
            $problems += "expected exactly 1 tallybook mention in server-main.log, got $($mentions.Count):"
            $problems += ($mentions | ForEach-Object Line)
        }
    }
    if (Test-Path $debugLog) {
        foreach ($marker in @("[tallybook] Loaded assembly", "Instantiate mod systems for tallybook")) {
            if (-not (Select-String -Path $debugLog -SimpleMatch $marker -Quiet)) { $problems += "missing debug-log marker: $marker" }
        }
    } elseif ($booted) { $problems += "server-debug.log missing" }

    if ($problems.Count -eq 0) {
        Write-Host " PASS"
        Remove-Item -Recurse -Force $dp -ErrorAction SilentlyContinue
    } else {
        Write-Host " FAIL"
        $problems | ForEach-Object { Write-Host "    $_" }
        Write-Host "    (data path kept for inspection: $dp)"
    }
    $results += @{ name = $name; ok = ($problems.Count -eq 0) }
}

Write-Host ""
$failed = @($results | Where-Object { -not $_.ok })
if ($failed.Count -gt 0) {
    Write-Host "COMPAT TEST FAILED: $($failed.name -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "COMPAT TEST PASSED: all $($results.Count) combos boot clean" -ForegroundColor Green
# Explicit success exit: the caller (version-sweep.ps1) reads $LASTEXITCODE, which only
# native commands and `exit` set. Without this, a -SkipBuild run that never invokes dotnet
# leaves a stale code behind and a fully passing matrix can be reported as FAIL.
exit 0
