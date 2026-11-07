#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Files,
    [Parameter(Mandatory)][string]$Version,
    [switch]$TestSigning
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'Authenticode verification requires Windows.' }
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Expected a stable three-part version.' }
if ($TestSigning -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
    throw 'Test-certificate trust is restricted to disposable GitHub-hosted Windows runners.'
}
if ($TestSigning) {
    $Principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The disposable signing-test runner must have administrator rights.' }
}
$Store = $null
$Added = [Collections.Generic.List[Security.Cryptography.X509Certificates.X509Certificate2]]::new()
try {
    if ($TestSigning) {
        # Trust only the returned self-signed test certificates for this check.
        # WinVerifyTrust must then validate the file digest normally; accepting
        # UnknownError/NotTrusted would hide errors unrelated to certificate trust.
        # CurrentUser\Root prompts for interactive confirmation on Windows.
        # GitHub-hosted runners are disposable administrators; their machine
        # store avoids that dialog and is restored in finally below.
        $Store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
        $Store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        foreach ($Path in $Files) {
            $Certificate = (Get-AuthenticodeSignature -LiteralPath $Path).SignerCertificate
            if ($null -eq $Certificate -or $Certificate.Subject -ne $Certificate.Issuer) { throw "Expected a self-signed SignPath test certificate: $Path" }
            if ($Store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $Certificate.Thumbprint, $false).Count -eq 0) {
                $Store.Add($Certificate)
                $Added.Add($Certificate)
            }
        }
    }
    foreach ($Path in $Files) {
        $File = Get-Item $Path -ErrorAction Stop
        $Info = $File.VersionInfo
        if ($Info.ProductName -ne 'WinPalm' -or $Info.CompanyName -ne 'RS Application' -or $Info.FileVersion -ne "$Version.0" -or $Info.ProductVersion -ne "$Version.0") {
            throw "Unexpected WinPalm product/version metadata: $Path"
        }
        $Signature = Get-AuthenticodeSignature -LiteralPath $File.FullName
        if ($null -eq $Signature.SignerCertificate -or $Signature.Status.ToString() -ne 'Valid') {
            throw "Invalid Authenticode signature on ${Path}: $($Signature.Status) $($Signature.StatusMessage)"
        }
        if (!$TestSigning) {
            if ($Signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'SignPath Foundation') {
                throw "Unexpected signing publisher: $Path"
            }
            if ($null -eq $Signature.TimeStamperCertificate) { throw "Missing Authenticode timestamp: $Path" }
        }
        Write-Output "Verified $($File.Name): $($Signature.Status), $($Signature.SignerCertificate.Subject)"
    }
} finally {
    if ($null -ne $Store) {
        foreach ($Certificate in $Added) { $Store.Remove($Certificate) }
        $Store.Close()
    }
}
