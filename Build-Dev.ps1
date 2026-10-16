# Builds Dev-BEM: BEM with the outer ring compiled in.
#
# One product, one branch, one codebase. Everything in BEM is in Dev-BEM, everything Advanced Mode
# adds is in Dev-BEM, and the outer ring is in Dev-BEM alone. -p:DevBem=true is the whole difference:
# it is a command line global property, which is the only thing that outranks the DevBem=false the
# project files state, so no environment variable and no stray build can turn the ring on by accident.
#
# The exe lands in output-dev\, never output\, because output\ is what the Nexus zip is packed from,
# and it is called "Bannerlord Environment Manager (Developer Build).exe" from the moment it is built.
# Never rename it afterwards: an unpackaged WinUI 3 app finds its own XAML through a PRI file named
# after the executable, so a renamed exe dies on its first page before a window exists.
#
# Usage:
#   .\Build-Dev.ps1            builds the single-file exe into output-dev\
#   .\Build-Dev.ps1 -Attachable builds Debug into bin\ instead, for a debugger session

[CmdletBinding()]
param(
    [switch] $Attachable,
    [ValidateSet('x64', 'x86', 'ARM64')]
    [string] $Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

Set-Location -LiteralPath $PSScriptRoot

$configuration = if ($Attachable) { 'Debug' } else { 'Release' }

Write-Host "Building Dev-BEM ($configuration, $Platform)."

# A Release build runs the AutoPublishSingleFileOnReleaseBuild target, which stops any running
# instance of the app it is about to overwrite. That is the developer exe, not the published one:
# they carry different single-instance keys and different output folders.
& dotnet build 'Bannerlord Environment Manager.csproj' -c $configuration -p:Platform=$Platform -p:DevBem=true

if ($LASTEXITCODE -ne 0) {
    throw "Dev-BEM build failed with exit code $LASTEXITCODE."
}

$built = if ($Attachable) {
    Join-Path $PSScriptRoot "bin\$Platform\Debug\net10.0-windows10.0.19041.0\win-$($Platform.ToLowerInvariant())\Bannerlord Environment Manager (Developer Build).exe"
} else {
    Join-Path $PSScriptRoot 'output-dev\Bannerlord Environment Manager (Developer Build).exe'
}

Write-Host "Dev-BEM is at: $built"
Write-Host 'Its title bar says Developer Build. The published BEM has no such suffix.'
