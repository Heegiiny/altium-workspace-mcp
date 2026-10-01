#!/usr/bin/env sh
# Generates the SOAP proxies of the vault service from tools/wsdl/VaultService.wsdl (the same as
# tools/generate-proxies.ps1 without the -Download switch): the result is src/AltiumWorkspaceMCP/Soap/Vault/VaultService.cs.
set -eu

root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/src/AltiumWorkspaceMCP/Soap/Vault/VaultService.cs"
temp="$(mktemp -d)"
trap 'rm -rf "$temp"' EXIT

export DOTNET_SVCUTIL_TELEMETRY_OPTOUT=1
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

cd "$root"
dotnet tool restore >/dev/null
dotnet tool run dotnet-svcutil -- "$root/tools/wsdl/VaultService.wsdl" \
    -d "$temp" -o VaultService.cs -n '*,AltiumWorkspaceMCP.Soap.Vault' --noLogo

mkdir -p "$(dirname "$output")"
{
    printf '%s\r\n' '// generated from the server WSDL by tools/generate-proxies.ps1 (dotnet-svcutil); do not edit by hand'
    # Remove the BOM that svcutil adds.
    sed '1s/^\xEF\xBB\xBF//' "$temp/VaultService.cs"
} > "$output"

echo "Done: $output"
