# Regression guard for inter-mod compatibility. Boots a headless Vintage Story server for
# each mod combination and fails on any [Error]/[Warning] in the server log or any missing/
# unexpected marker.
#
# PORTABLE: nothing here names a specific mod. The project is whichever folder under the repo
# root holds a modinfo.json, and the modid, version and assembly name are read from it. Copy
# this file and version-sweep.ps1 into a new mod's repo and they work as-is; the only thing to
# edit is the companion set below.
#
# For a client-side mod, what a dedicated-server boot proves is narrower than for a universal
# one — but still real: the server unpacks the zip, loads the assembly and instantiates its
# ModSystems (visible in server-debug.log) before ShouldLoad gates them off. That catches a
# broken zip, a bad modinfo/dependency declaration, an assembly that no longer loads against
# the target game version, and any accidental loss of the client-only gate. What it can NOT
# catch is client-side behaviour — registry reads, input events, GUI. Those stay on a manual
# checklist in README.md, and a mod is not tested until that checklist is run too.
#
# Invariants enforced per combo:
#   - server reaches "Dedicated Server now running"
#   - zero [Error]/[Warning] lines in server-main.log
#   - our modid and every expected companion modid appear in the "Mods, sorted by
#     dependency:" line, and the "Found N mods (0 disabled)" count is exact
#   - server-debug.log shows our assembly loaded and mod systems instantiated
#   - total server-side silence: exactly ONE mention of our modid in server-main.log (the
#     dependency-sort line). A second mention means the mod started logging/running on the
#     server — e.g. the Client side gate was lost — and fails the combo. Drop this one check
#     for a universal mod, which is *supposed* to run there; keep everything else.
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

# Everything about "which mod is this" is derived, never spelled out, so this script can be
# copied into another mod's repo unchanged: the project is whichever folder holds a
# modinfo.json, and the modid, version and assembly name come from that plus the csproj.
$projDir = Get-ChildItem $root -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "modinfo.json") } |
    Select-Object -First 1
if (-not $projDir) { throw "No mod project found under $root (expected a folder containing modinfo.json)" }

$info    = Get-Content "$($projDir.FullName)\modinfo.json" -Raw | ConvertFrom-Json
$modid   = $info.modid
$version = $info.version
if (-not $modid)   { throw "modinfo.json has no modid" }
if (-not $version) { throw "modinfo.json has no version" }

$csproj  = Get-ChildItem $projDir.FullName -Filter *.csproj | Select-Object -First 1
if (-not $csproj) { throw "No .csproj in $($projDir.FullName)" }
$dllName = [IO.Path]::GetFileNameWithoutExtension($csproj.Name) + ".dll"

$ourZip = "$root\dist\${modid}_$version.zip"

if (-not $SkipBuild) {
    # System dotnet is SDK 9 and refuses the net10.0 game references; prefer the user-scoped SDK.
    $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
    & $dotnet build $csproj.FullName -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $staging = "$env:TEMP\$modid-pack"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force $staging | Out-Null
    Copy-Item "$($projDir.FullName)\modinfo.json" $staging
    Copy-Item "$($projDir.FullName)\bin\Release\$dllName" $staging
    New-Item -ItemType Directory -Force "$root\dist" | Out-Null
    Compress-Archive -Path "$staging\*" -DestinationPath $ourZip -Force
}
if (-not (Test-Path $ourZip)) { throw "Mod zip not found: $ourZip" }

# A port nobody is listening on: bind to 0, let the OS choose, release it, use that number.
# Racy in principle, harmless here — the window is milliseconds and a clash just reports the
# environment problem below rather than a mod failure.
function Get-FreePort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $p = $l.LocalEndpoint.Port
    $l.Stop()
    return $p
}

# Fetch a companion mod zip: cache -> live Mods folder -> mods a server sent us -> mod DB API
function Get-CompatMod([string]$modid, [string]$filePattern) {
    $cached = Get-ChildItem $cache -Filter $filePattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cached) { return $cached.FullName }

    # Newest first, because ModsByServer accumulates a folder per server and keeps every
    # version each one ever pushed; the oldest copy is not what anybody is playing.
    $live = Get-ChildItem "$env:APPDATA\VintagestoryData\Mods", "$env:APPDATA\VintagestoryData\ModsByServer" `
        -Recurse -Filter $filePattern -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
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
# Add here as the surface grows (HUD-corner mods once §5 ships), e.g.:
#   somemodid = Get-CompatMod "somemodid" "somemodid_*.zip"
#
# betterruins: a recipe-adding content mod, which is the category the registry reads care
# about. It adds hundreds of grid recipes for items vanilla already makes, gated behind
# schematics — exactly the shape that made "is this a second recipe or the same one?" a real
# question rather than a theoretical one. What this combo proves is narrow but load-bearing:
# a server pushing that many extra recipes still leaves Tallybook completely silent
# server-side. Whether we group them correctly is a client question and stays on the manual
# checklist.
Write-Host "Collecting companion mods..."
$mods = [ordered]@{}
$mods.betterruins = Get-CompatMod "betterruins" "BetterRuins*.zip"
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
    # The modid must NOT appear in the dir name: the server logs the Mods search path into
    # server-main.log, which would trip the exactly-one-mention silence check below.
    #
    # The PID keeps concurrent runs apart. version-sweep.ps1 invokes this script repeatedly,
    # and a second run started by hand while a sweep is going would otherwise delete the
    # sweep's data directory mid-boot — producing a phantom "server did not start" failure
    # against a mod that is perfectly fine.
    $dp = "$env:TEMP\vsmod-compat-$PID-$name"
    if (Test-Path $dp) { Remove-Item -Recurse -Force $dp }
    New-Item -ItemType Directory -Force "$dp\Mods" | Out-Null
    Copy-Item $ourZip "$dp\Mods"
    foreach ($id in $combo.expect) { Copy-Item $mods[$id] "$dp\Mods" }

    # Give each run its own port. The default 42420 is also what a singleplayer world binds,
    # so running the matrix while the game is open failed every combo with an empty log —
    # which reads exactly like the mod being broken, and is not (found by Mark).
    #
    # Via --port, NOT by writing serverconfig.json: a hand-written partial config is not
    # merged with the defaults, it replaces them, and the server dies in the configuration
    # phase on "default group code suplayer but no such group exists". Let the server author
    # its own config and override the one value on the command line.
    $port = Get-FreePort

    $proc = Start-Process $ServerExe -ArgumentList "--dataPath", $dp, "--port", $port -PassThru -WindowStyle Hidden
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

    # An environment problem is not a mod failure, and reporting it as one wastes an evening
    # chasing a bug that is not there. Say what actually happened and stop.
    $crashLog = "$dp\Logs\server-crash.log"
    if (-not $booted -and (Test-Path $crashLog)) {
        $crash = Get-Content $crashLog -Raw
        if ($crash -match "SocketException|Only one usage of each socket address") {
            Write-Host " BLOCKED"
            Write-Host "    another Vintage Story server is already using the port (a singleplayer"
            Write-Host "    world counts) — close it and re-run. This is not a mod failure."
            Remove-Item -Recurse -Force $dp -ErrorAction SilentlyContinue
            $results += @{ name = $name; ok = $false; blocked = $true }
            continue
        }
    }

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
        foreach ($id in (@($modid) + $combo.expect)) {
            if ($sortLine -notmatch "[ ,]$id(,|`$| )") { $problems += "modid '$id' missing from load order: $sortLine" }
        }

        # Server-side silence: the sort line must be the ONLY mention of our modid in the main
        # log. Path echoes don't count as mod output — the server logs its Mods search path
        # and asset origins, and those sit under directories that often contain the modid
        # (a repo folder named after the mod, a cached server package inside it). Blank the
        # known paths out of each line before matching, so the check tests what the mod said
        # rather than where it lives.
        $serverRoot = Split-Path $ServerExe -Parent
        $mentions = @(Get-Content $log | Where-Object {
            ($_ -replace [regex]::Escape($serverRoot), "" `
                -replace [regex]::Escape($dp), "" `
                -replace [regex]::Escape($root), "") -match $modid
        } | ForEach-Object { [pscustomobject]@{ Line = $_ } })
        if ($mentions.Count -ne 1) {
            $problems += "expected exactly 1 '$modid' mention in server-main.log, got $($mentions.Count):"
            $problems += ($mentions | ForEach-Object Line)
        }
    }
    if (Test-Path $debugLog) {
        foreach ($marker in @("[$modid] Loaded assembly", "Instantiate mod systems for $modid")) {
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
    $results += @{ name = $name; ok = ($problems.Count -eq 0); blocked = $false }
}

Write-Host ""

# BLOCKED is reported apart from FAIL and with its own exit code, because "the mod is broken"
# and "this machine could not run the test" call for completely different next actions — and
# a blocked run dressed up as a failure sends you hunting a bug that does not exist.
$blocked = @($results | Where-Object { $_.blocked })
if ($blocked.Count -gt 0) {
    Write-Host "COMPAT TEST BLOCKED: $($blocked.name -join ', ') — environment, not the mod." -ForegroundColor Yellow
    exit 2
}

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
