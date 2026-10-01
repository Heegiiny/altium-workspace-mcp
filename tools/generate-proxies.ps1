<#
.SYNOPSIS
  Generates the SOAP proxies of the vault service (VaultService.svc) from the server WSDL.

.DESCRIPTION
  The server publishes the description of its service openly: GET /vault/VaultService.svc?singleWsdl.
  The proxies are built by dotnet-svcutil (a local tool from .config/dotnet-tools.json) into
  the namespace AltiumWorkspaceMCP.Soap.Vault; the result is one file
  src/AltiumWorkspaceMCP/Soap/Vault/VaultService.cs, not edited by hand.

  The login services (IDS) do not publish a WSDL, their contracts are hand-written in Soap/Ids/IdsContracts.cs.

  A normal run works from the saved tools/wsdl/VaultService.wsdl and does not need the server.
  With the -Download switch the WSDL file is first downloaded from the server (logging in with the current Windows
  account) — this is how the proxies are updated for a new server version. The server addresses in the downloaded
  description are replaced with localhost: internal names do not get into the repository.

  The svcutil parameters are the defaults, only the namespace changes: requests and responses as
  XxxRequest/XxxResponse types with the SessionID/APIVersion headers and, next to them, overloads
  with ordinary parameters, asynchronous methods only, shared collections (_ALU_ItemList etc.).
  This is how the client looked before, so the calling code did not change.
  English messages and a stable result are given by DOTNET_SYSTEM_GLOBALIZATION_INVARIANT.

.PARAMETER Download
  Download the WSDL from the server (ALTIUM_BASE_URL or -BaseUrl) before generating.
#>
[CmdletBinding()]
param(
    [switch]$Download,
    [string]$BaseUrl = $env:ALTIUM_BASE_URL,
    [string]$Wsdl = (Join-Path $PSScriptRoot 'wsdl\VaultService.wsdl'),
    [string]$Output = (Join-Path $PSScriptRoot '..\src\AltiumWorkspaceMCP\Soap\Vault\VaultService.cs')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if ($Download) {
    if (-not $BaseUrl) { throw 'The server address is not set: specify -BaseUrl or the ALTIUM_BASE_URL variable.' }
    $url = $BaseUrl.TrimEnd('/') + '/vault/VaultService.svc?singleWsdl'
    Write-Host "Downloading $url"
    $text = (Invoke-WebRequest -Uri $url -UseDefaultCredentials -UseBasicParsing).Content
    # Service port addresses are internal server names; they are not needed for generation.
    $text = [regex]::Replace($text, '(<soap:address location=")http(s?)://[^/"]+(/vault/VaultService\.svc")', '$1http$2://localhost$3')
    [System.IO.File]::WriteAllText($Wsdl, $text, [System.Text.UTF8Encoding]::new($false))
}

if (-not (Test-Path $Wsdl)) { throw "WSDL not found: $Wsdl" }

$env:DOTNET_SVCUTIL_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'

Push-Location $root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("svcutil-" + [guid]::NewGuid().ToString('N'))
    dotnet tool run dotnet-svcutil -- (Resolve-Path $Wsdl).Path `
        -d $temp -o VaultService.cs -n '*,AltiumWorkspaceMCP.Soap.Vault' --noLogo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet-svcutil finished with an error' }

    $header = "// generated from the server WSDL by tools/generate-proxies.ps1 (dotnet-svcutil); do not edit by hand`r`n"
    $code = [System.IO.File]::ReadAllText((Join-Path $temp 'VaultService.cs')).TrimStart([char]0xFEFF)
    New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
    [System.IO.File]::WriteAllText($Output, $header + $code, [System.Text.UTF8Encoding]::new($false))
    Remove-Item $temp -Recurse -Force
}
finally {
    Pop-Location
}

Write-Host "Done: $((Resolve-Path $Output).Path)" -ForegroundColor Green
