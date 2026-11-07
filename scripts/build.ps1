#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [string]$DevelopmentDirectory = (Join-Path $PSScriptRoot '../../winpalm-dev'),
    [switch]$Package,
    [switch]$Installer,
    [switch]$SkipChecks
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Dev = [IO.Path]::GetFullPath($DevelopmentDirectory)
if ($Dev.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $Dev -eq $Root) { throw 'Keep development output outside the source repository.' }
$Cache = Join-Path $Dev 'cache'
$Output = Join-Path $Dev "artifacts/builds/$Architecture"
$env:CARGO_TARGET_DIR = Join-Path $Cache 'cargo'
$Web = Join-Path $Cache 'phone-workspace'
$WebOutput = Join-Path $Dev 'artifacts/web'
$Rid = "win-$Architecture"
$Target = if ($Architecture -eq 'x64') { 'x86_64-pc-windows-msvc' } else { 'aarch64-pc-windows-msvc' }
function Run([scriptblock]$Command) { & $Command; if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Command" } }
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Cache, $Output, $Web | Out-Null
# Build a disposable phone workspace; source stays free of node_modules/dist.
foreach ($Name in @('src','public','test')) {
    $Destination = Join-Path $Web $Name
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    Copy-Item (Join-Path $Root "web/$Name") $Destination -Recurse
}
Get-ChildItem (Join-Path $Root 'web') -File | Copy-Item -Destination $Web
Push-Location $Web
try {
    Run { bun install --frozen-lockfile }
    if (!$SkipChecks) { Run { bun run lint }; Run { bun run test } }
    $env:WINPALM_WEB_OUT = $WebOutput
    Run { bun run build }
} finally { Pop-Location; Remove-Item Env:WINPALM_WEB_OUT -ErrorAction SilentlyContinue }
Push-Location $Root
try {
    Run { rustup target add $Target }
    if (!$SkipChecks) {
        Run { bun scripts/release-notes.ts check ((Get-Content (Join-Path $Root 'web/package.json') -Raw | ConvertFrom-Json).version) }
        Run { bun (Join-Path $Web 'node_modules/oxlint/bin/oxlint') scripts }
        Run { bun (Join-Path $Web 'node_modules/typescript/bin/tsc') --project scripts --typeRoots (Join-Path $Web 'node_modules/@types') }
        Run { bun test scripts }
        Run { dotnet run --project windows/WinPalm.UpdateTests -c Release --artifacts-path (Join-Path $Cache 'dotnet-update-tests') }
        Run { cargo fmt --all --check }
        Run { cargo clippy --locked --all-targets -- -D warnings }
        Run { cargo test --locked --all-targets }
    }
    Run { cargo build --locked --release --target $Target }
    Run { dotnet publish windows/WinPalm/WinPalm.csproj -c Release -r $Rid --self-contained true -p:RestoreLockedMode=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:CopyOutputSymbolsToPublishDirectory=false --artifacts-path (Join-Path $Cache 'dotnet') --output $Output }
    # Serve the exact working source alongside the phone UI (AGPL section 13).
    $Version = (Get-Content (Join-Path $Root 'web/package.json') -Raw | ConvertFrom-Json).version
    $SourceArchive = Join-Path $Dev "artifacts/WinPalm-$Version-source.zip"
    $SourceFiles = @(git -C $Root ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $SourceFiles.Count -eq 0) { throw 'Git and a source checkout are required to package corresponding source.' }
    if (Test-Path $SourceArchive) { Remove-Item $SourceArchive -Force }
    $SourceZip = [IO.Compression.ZipFile]::Open($SourceArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($Relative in $SourceFiles) {
            $SourceFile = Join-Path $Root $Relative
            if (Test-Path $SourceFile -PathType Leaf) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($SourceZip, $SourceFile, $Relative.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null }
        }
    } finally { $SourceZip.Dispose() }
    Copy-Item $SourceArchive (Join-Path $WebOutput 'source.zip') -Force
    Copy-Item (Join-Path $env:CARGO_TARGET_DIR "$Target/release/winpalm_core.dll") $Output -Force
    if (Test-Path (Join-Path $Output 'web')) { Remove-Item (Join-Path $Output 'web') -Recurse -Force }
    Copy-Item $WebOutput (Join-Path $Output 'web') -Recurse
    foreach ($Name in @('LICENSE','NOTICE','THIRD_PARTY_NOTICES.md','README.md','SECURITY.md')) { Copy-Item (Join-Path $Root $Name) $Output -Force }
    Copy-Item (Join-Path $Root 'packaging/Test-WinPalm.cmd') $Output -Force
    if (!(Test-Path (Join-Path $Output 'WinPalm.exe')) -or !(Test-Path (Join-Path $Output 'winpalm_core.dll')) -or !(Test-Path (Join-Path $Output 'web/index.html'))) { throw 'Incomplete Windows package' }
    foreach ($Name in @('WinPalm.exe','winpalm_core.dll')) {
        $Info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Output $Name))
        if ($Info.ProductName -ne 'WinPalm' -or $Info.CompanyName -ne 'RS Application' -or $Info.FileVersion -ne "$Version.0" -or $Info.ProductVersion -ne "$Version.0") {
            throw "Missing or incorrect signing metadata in $Name"
        }
    }
    if ($Architecture -eq $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())) {
        $Report = Join-Path $Dev "artifacts/self-test-$Architecture.json"
        $Process = Start-Process (Join-Path $Output 'WinPalm.exe') -ArgumentList @('--self-test','--report',('"' + $Report + '"')) -Wait -PassThru
        if ($Process.ExitCode -ne 0 -or !(Test-Path $Report) -or !((Get-Content $Report -Raw | ConvertFrom-Json).ok)) { throw "Windows self-test failed. See $Report" }
    }
    if ($Package) {
        $Version = ((Get-Content (Join-Path $Root 'web/package.json') -Raw | ConvertFrom-Json).version)
        $Archive = Join-Path $Dev "artifacts/WinPalm-$Version-$Rid.zip"
        # ZipFile writes portable forward-slash entry names.
        if (Test-Path $Archive) { Remove-Item $Archive -Force }
        [IO.Compression.ZipFile]::CreateFromDirectory($Output, $Archive, [IO.Compression.CompressionLevel]::Optimal, $false)
        (Get-FileHash $Archive -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($Archive) | Set-Content ($Archive + '.sha256')
        Run { dotnet run --project windows/WinPalm.UpdateTests -c Release --artifacts-path (Join-Path $Cache 'dotnet-update-tests') -- --package $Archive $Architecture $Version }
        Write-Output $Archive
    }
    if ($Installer) {
        $Payloads = Join-Path $Dev 'artifacts/builds'
        foreach ($Arch in @('x64','arm64')) {
            if (!(Test-Path (Join-Path $Payloads "$Arch/WinPalm.exe"))) { throw 'Build both x64 and ARM64 packages before using -Installer. Install NSIS 3 or set MAKENSIS to its compiler.' }
        }
        Run { bun scripts/build-installer.ts --payloads $Payloads --output (Join-Path $Dev "artifacts/WinPalm-$Version-Setup.exe") }
    }
} finally { Pop-Location }
Write-Output "Windows package: $Output"
