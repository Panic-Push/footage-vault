# Installs the built setup.exe on the GitHub Windows machine, opens each screen with sample footage,
# takes screenshots, and reports crashes or logged errors as annotations.
$ErrorActionPreference = 'Stop'
$setup = Resolve-Path 'installer\Output\FootageVault-Setup.exe'
$shots = Join-Path $env:RUNNER_TEMP 'screens'
New-Item -ItemType Directory -Force $shots | Out-Null
$failed = $false
function Fail([string]$m) { Write-Host "::error title=Smoke test::$m"; $script:failed = $true }

# 1. Silent install (per-user), check the Start menu entry used by Windows search
Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=""' -Wait
$exe = Join-Path $env:LOCALAPPDATA 'Programs\Footage Vault\FootageVault.exe'
if (-not (Test-Path $exe)) { Fail "Not installed at $exe"; exit 1 }
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Footage Vault.lnk'
if (-not (Test-Path $lnk)) { Fail "Start menu shortcut missing: $lnk" } else { Write-Host "Start menu shortcut OK" }
$uninstall = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | ForEach-Object { Get-ItemProperty $_.PSPath } |
    Where-Object { $_.DisplayName -eq 'Footage Vault' }
if (-not $uninstall) { Fail 'Not listed under Settings > Apps (no uninstall entry)' } else { Write-Host "Apps list entry OK" }

# 2. Sample footage: a story with two phone sub-folders, plus a second story
$root = Join-Path $env:RUNNER_TEMP 'Footage'
$files = @('Collector Visit\VID_0081.MOV', 'Collector Visit\VID_0082.MOV', 'Collector Visit\Phone 1\IMG_4410.MP4',
           'Collector Visit\Phone 1\IMG_4411.MP4', 'Collector Visit\Phone 2\VID_20261026.mp4', 'Flood Relief\clip1.mp4',
           'Flood Relief\clip2.mp4', 'Flood Relief\notes.txt')
$rng = [System.Random]::new(7)
foreach ($f in $files) {
    $p = Join-Path $root $f
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    $bytes = New-Object byte[] (200000 + $rng.Next(100000)); $rng.NextBytes($bytes)
    [IO.File]::WriteAllBytes($p, $bytes)
}

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
function Shot([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Run([string]$name, [string[]]$appArgs, [int]$wait = 12) {
    $p = Start-Process $exe -ArgumentList $appArgs -PassThru
    Start-Sleep -Seconds $wait
    if ($p.HasExited) { Fail "App closed by itself on '$name' (exit code $($p.ExitCode))" }
    Shot $name
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep 2 }
}

Run 'home' @('--page', 'home') 15
Run 'preview' @('--scan', "`"$root\Collector Visit`"", "`"$root\Flood Relief`"") 20
Run 'queue' @('--page', 'queue')
Run 'history' @('--page', 'history')
Run 'settings' @('--page', 'settings')

# 3. Logged errors
$logs = Join-Path $env:LOCALAPPDATA 'FootageVault\logs'
if (Test-Path $logs) {
    $errs = Get-ChildItem $logs -Filter 'app-*.log' | Get-Content | Where-Object { $_ -match ' ERROR ' } | Select-Object -First 15
    foreach ($e in $errs) { Fail ($e -replace '%', '%25') }
    Get-ChildItem $logs -Filter 'app-*.log' | Get-Content | Select-Object -Last 30 | Out-Host
}
# Windows Error Reporting / .NET crash events from the last minutes
$since = (Get-Date).AddMinutes(-10)
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error' -and $_.Message -match 'FootageVault' } |
    Select-Object -First 3 | ForEach-Object { Fail (($_.Message -split "`n" | Select-Object -First 12) -join '%0A') }

Write-Host "Screenshots in $shots"
if ($failed) { exit 1 }
