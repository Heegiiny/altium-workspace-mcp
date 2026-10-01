# Builds the server into the Release directory in the repository root:
#   <repository>\Release\altium-vault-mcp.exe
# This path is what goes into the settings of Claude Desktop, Claude Code and the bridge.
#
# A server running from Release holds its files, so before building close
# Claude Desktop (from the tray) and stop the bridge if it runs from this directory.

param(
    [string] $Output = (Join-Path $PSScriptRoot 'Release')
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\AltiumWorkspaceMCP\AltiumWorkspaceMCP.csproj'
$exe = Join-Path $Output 'altium-vault-mcp.exe'

$running = Get-Process altium-vault-mcp -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) }

if ($running) {
    throw "The server is running from $Output (PID $(($running.Id) -join ', ')). " +
        'Close Claude Desktop and stop the bridge, then repeat the build.'
}

# The intermediate build bypasses bin: the bridge or an earlier server instance may be running
# from there, and busy files would break the build.
$intermediate = Join-Path $PSScriptRoot 'src\AltiumWorkspaceMCP\obj\publish-build\'

dotnet publish $project -c Release -o $Output "-p:OutDir=$intermediate" --nologo

if ($LASTEXITCODE -ne 0) {
    throw 'The build failed, details above.'
}

Write-Host ''
Write-Host "Done: $exe"
