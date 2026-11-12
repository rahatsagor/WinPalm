#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# This check installs and removes a real app. Never run it on an owner's PC.
if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer checks require a disposable GitHub-hosted Windows runner.'
}
$Version = $Version -replace '^v', ''
$Setup = (Resolve-Path $Setup).Path
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
$TemporaryRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (!$WorkDirectory.StartsWith($TemporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep check reports in RUNNER_TEMP.' }
$Install = Join-Path $env:LOCALAPPDATA 'Programs/WinPalm'
$Data = Join-Path $env:LOCALAPPDATA 'RS Application/WinPalm'
$Registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinPalm'
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'WinPalm/WinPalm.lnk'
if ((Test-Path $Install) -or (Test-Path $Registration) -or (Get-Process WinPalm -ErrorAction SilentlyContinue)) { throw 'This runner already has a WinPalm installation.' }
New-Item -ItemType Directory -Force $WorkDirectory, $Data | Out-Null
$Marker = Join-Path $Data ('installer-check-' + [Guid]::NewGuid().ToString('N'))
$MarkerContents = [Guid]::NewGuid().ToString('N')
Set-Content $Marker $MarkerContents -NoNewline
$Checks = [Collections.Generic.List[string]]::new()
$Report = [ordered]@{ ok = $false; architecture = $Architecture; version = $Version; checks = $Checks }
$App = $null
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; $Checks.Add($Message); Write-Output "PASS $Message" }
function Install-Setup {
    Write-Output 'Running setup /S'
    $Process = Start-Process $Setup -ArgumentList '/S' -PassThru
    if (!$Process.WaitForExit(90000)) {
        $Process.Kill($true)
        throw 'Setup did not finish within 90 seconds.'
    }
    if ($Process.ExitCode -ne 0) { throw "Setup exited with $($Process.ExitCode)." }
}
try {
    Install-Setup
    foreach ($File in @('WinPalm.exe','winpalm_core.dll','web/index.html','web/source.zip','Resources/WinPalm.ico','NSIS-LICENSE.txt','Uninstall.exe')) {
        Require (Test-Path (Join-Path $Install $File) -PathType Leaf) "setup installs $File"
    }
    $Installed = Get-ItemProperty $Registration
    Require ($Installed.DisplayVersion -eq $Version -and $Installed.InstallLocation -eq $Install) 'Installed apps records the expected version and path'
    Require (Test-Path $Shortcut -PathType Leaf) 'Start menu shortcut exists'
    Require ((Get-Content $Marker -Raw) -eq $MarkerContents) 'first install preserves private app data'

    $SelfTest = Join-Path $WorkDirectory 'installed-self-test.json'
    Write-Output 'Checking the installed app'
    $Process = Start-Process (Join-Path $Install 'WinPalm.exe') -ArgumentList @('--self-test','--report',('"' + $SelfTest + '"')) -PassThru
    if (!$Process.WaitForExit(90000)) {
        $Process.Kill($true)
        throw 'The installed app integration check did not finish within 90 seconds.'
    }
    Require ($Process.ExitCode -eq 0 -and (Test-Path $SelfTest)) 'installed self-contained app runs its integration checks'
    $Result = Get-Content $SelfTest -Raw | ConvertFrom-Json
    Require ($Result.ok -and $Result.runtime.ToLowerInvariant() -eq $Architecture) 'setup selects the native architecture'

    $App = Start-Process (Join-Path $Install 'WinPalm.exe') -ArgumentList '--start-minimized' -PassThru
    Require ($App.WaitForInputIdle(30000) -and !$App.HasExited) 'installed tray app starts'
    $Identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $QuitName = 'Local\RSApplication.WinPalm.Quit.' + $Identity
    $ReadyTimer = [Diagnostics.Stopwatch]::StartNew()
    $QuitReady = $false
    for ($Attempt = 0; $Attempt -lt 300 -and !$App.HasExited; $Attempt++) {
        $QuitEvent = $null
        if ([Threading.EventWaitHandle]::TryOpenExisting($QuitName, [ref]$QuitEvent)) {
            $QuitEvent.Dispose()
            $QuitReady = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    $Report['quitEventReadyAfterMs'] = $ReadyTimer.ElapsedMilliseconds
    Require $QuitReady 'installed tray app creates its installer quit event'
    Write-Output 'Testing the quit helper directly'
    $QuitTimer = [Diagnostics.Stopwatch]::StartNew()
    $Helper = Start-Process (Join-Path $Install 'WinPalm.exe') -ArgumentList '--quit-for-install' -PassThru
    Require ($Helper.WaitForExit(60000)) 'installer quit helper completes'
    $Report['directQuitHelperExitCode'] = $Helper.ExitCode
    $Report['directQuitHelperElapsedMs'] = $QuitTimer.ElapsedMilliseconds
    Require ($Helper.ExitCode -eq 0 -and $App.WaitForExit(15000) -and $App.ExitCode -eq 0) 'installer quit helper gracefully closes the running tray app'
    $App = Start-Process (Join-Path $Install 'WinPalm.exe') -ArgumentList '--start-minimized' -PassThru
    Require ($App.WaitForInputIdle(30000) -and !$App.HasExited) 'tray app restarts before actual reinstall'
    $QuitReady = $false
    for ($Attempt = 0; $Attempt -lt 300 -and !$App.HasExited; $Attempt++) {
        $QuitEvent = $null
        if ([Threading.EventWaitHandle]::TryOpenExisting($QuitName, [ref]$QuitEvent)) {
            $QuitEvent.Dispose()
            $QuitReady = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    Require $QuitReady 'restarted tray app is ready for setup'
    Install-Setup
    Require ($App.WaitForExit(15000) -and $App.ExitCode -eq 0) 'reinstall gracefully closes the running tray app'
    Require ((Get-Content $Marker -Raw) -eq $MarkerContents) 'reinstall preserves private app data'

    Write-Output 'Checking uninstall /S'
    $Process = Start-Process (Join-Path $Install 'Uninstall.exe') -ArgumentList '/S' -PassThru
    if (!$Process.WaitForExit(90000)) {
        $Process.Kill($true)
        throw 'The uninstaller did not finish within 90 seconds.'
    }
    # NSIS restarts its uninstaller from a temporary directory. Wait for that
    # child to remove the registration and program folder as well.
    for ($Attempt = 0; $Attempt -lt 150 -and ((Test-Path $Install) -or (Test-Path $Registration)); $Attempt++) { Start-Sleep -Milliseconds 200 }
    Require ($Process.ExitCode -eq 0) 'silent uninstall succeeds'
    Require (!(Test-Path $Install) -and !(Test-Path $Registration) -and !(Test-Path $Shortcut)) 'uninstall removes program files, registration and shortcut'
    Require ((Get-Content $Marker -Raw) -eq $MarkerContents) 'uninstall retains private app data'
    $Report.ok = $true
} catch {
    $Report['error'] = $_.Exception.Message
    throw
} finally {
    if ($null -ne $App) {
        $App.Refresh()
        $Report['trayProcessExited'] = $App.HasExited
        if ($App.HasExited) { $Report['trayProcessExitCode'] = $App.ExitCode }
    }
    $Log = Join-Path $Data 'WinPalm.log'
    if (Test-Path $Log) { Copy-Item $Log (Join-Path $WorkDirectory 'installed-app.log') }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime'; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -like '*WinPalm*' } |
        Select-Object TimeCreated, Id, Message |
        Format-List | Out-File (Join-Path $WorkDirectory 'runtime-events.txt')
    $Report['processesAfterCheck'] = @(Get-Process WinPalm -ErrorAction SilentlyContinue | ForEach-Object {
        [ordered]@{ id = $_.Id; path = $_.Path; responding = $_.Responding }
    })
    $Report['installedFilesAfterCheck'] = @(Get-ChildItem $Install -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    $Report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $WorkDirectory 'installer-check.json')
    if ($null -ne $App -and !$App.HasExited) { Stop-Process -Id $App.Id -Force }
}
