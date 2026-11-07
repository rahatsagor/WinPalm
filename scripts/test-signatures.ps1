#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Signature checks require a disposable GitHub-hosted Windows runner.'
}
$Work = Join-Path $env:RUNNER_TEMP ('winpalm-signature-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $Work | Out-Null
$Verify = Join-Path $PSScriptRoot 'verify-signatures.ps1'
$Certificate = $null
function Expect-Failure([scriptblock]$Command, [string]$Expected) {
    try { & $Command } catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        Write-Output "PASS rejected $Expected"
        return
    }
    throw "Expected rejection: $Expected"
}
try {
    $Files = @('WinPalm.exe','winpalm_core.dll') | ForEach-Object {
        $Path = Join-Path $Work $_
        Copy-Item (Join-Path $PackageDirectory $_) $Path
        $Path
    }
    Expect-Failure { & $Verify -Files $Files -Version $Version } 'Invalid Authenticode signature'

    $Certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=WinPalm CI test only' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays(1)
    foreach ($Path in $Files) { $null = Set-AuthenticodeSignature -FilePath $Path -Certificate $Certificate -HashAlgorithm SHA256 }
    & $Verify -Files $Files -Version $Version -TestSigning
    if (Test-Path "Cert:\LocalMachine\Root\$($Certificate.Thumbprint)") { throw 'Test verifier left its certificate trusted.' }
    Write-Output 'PASS signed native app and DLL; temporary certificate trust removed'

    $RootStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    $RootStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $RootStore.Add($Certificate)
        Expect-Failure { & $Verify -Files $Files -Version $Version } 'Unexpected signing publisher'
    } finally { $RootStore.Remove($Certificate); $RootStore.Close() }

    Expect-Failure { & $Verify -Files $Files -Version '65534.0.0' -TestSigning } 'Unexpected WinPalm product/version metadata'
    $Stream = [IO.File]::Open($Files[0], [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
    try {
        $Stream.Position = 4096
        $Byte = $Stream.ReadByte()
        if ($Byte -lt 0) { throw 'Test executable is too small.' }
        $Stream.Position = 4096
        $Stream.WriteByte($Byte -bxor 1)
    } finally { $Stream.Dispose() }
    Expect-Failure { & $Verify -Files $Files -Version $Version -TestSigning } 'Invalid Authenticode signature'
    if (Test-Path "Cert:\LocalMachine\Root\$($Certificate.Thumbprint)") { throw 'Failed verification left its test certificate trusted.' }
    Write-Output 'PASS modified signed executable refused; certificate cleanup also runs after failure'
} finally {
    if ($null -ne $Certificate) { Remove-Item "Cert:\CurrentUser\My\$($Certificate.Thumbprint)" -ErrorAction SilentlyContinue }
    Remove-Item $Work -Recurse -Force
}
