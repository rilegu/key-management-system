#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the server as a Windows Service.

.DESCRIPTION
    Publishes the server, copies it to an installation directory, and registers it with the
    service manager. Secrets are set as service-scoped environment variables rather than written
    into appsettings.json, so a configuration file can be read by anyone who can read the folder
    without handing them the signing key.

    Run from an elevated prompt.

.PARAMETER InstallPath
    Where to install. Defaults to C:\Program Files\Key Management.

.PARAMETER Role
    Which parts this service runs: All, Api or Gateway. Defaults to All.

.PARAMETER Name
    Service name. Defaults to KeyManagement, or KeyManagement.<Role> for a split deployment.

.PARAMETER SigningKey
    The token signing key, at least 32 bytes.

.PARAMETER CertificatePassword
    Protects the device certificates' private keys.

.EXAMPLE
    powershell -File scripts/install-service.ps1 -SigningKey (openssl rand -base64 32) -CertificatePassword 'something'

.EXAMPLE
    Two services against one database, so cabinets stay attached across an API restart:

    powershell -File scripts/install-service.ps1 -Role Api     -SigningKey ... -CertificatePassword ...
    powershell -File scripts/install-service.ps1 -Role Gateway -SigningKey ... -CertificatePassword ...
#>
[CmdletBinding()]
param(
    [string]$InstallPath = 'C:\Program Files\Key Management',
    [ValidateSet('All', 'Api', 'Gateway')]
    [string]$Role = 'All',
    [string]$Name,
    [Parameter(Mandatory = $true)][string]$SigningKey,
    [Parameter(Mandatory = $true)][string]$CertificatePassword
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Registering a service needs an elevated prompt.'
}

if ([Text.Encoding]::UTF8.GetByteCount($SigningKey) -lt 32) {
    throw 'The signing key must be at least 32 bytes.'
}

if (-not $Name) {
    $Name = if ($Role -eq 'All') { 'KeyManagement' } else { "KeyManagement.$Role" }
}

$root = Split-Path -Parent $PSScriptRoot
$target = if ($Role -eq 'All') { $InstallPath } else { Join-Path $InstallPath $Role }

Write-Host "Publishing to $target..."
& dotnet publish (Join-Path $root 'src\KeyManagement.Server') `
    --configuration Release --output $target --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'The publish failed.' }

$exe = Join-Path $target 'KeyManagement.Server.exe'
if (-not (Test-Path $exe)) { throw "Published output is missing $exe." }

$existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping and removing the existing $Name service..."
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $Name -Force }
    & sc.exe delete $Name | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Registering $Name..."
New-Service -Name $Name -BinaryPathName "`"$exe`"" -DisplayName "Key Management ($Role)" `
    -Description 'On-premises key and asset custody.' -StartupType Automatic | Out-Null

# Service-scoped, so they never appear in a configuration file. The double null terminator is
# what the service manager expects for a multi-string value.
$environment = @(
    "Hosting__Role=$Role",
    "Jwt__SigningKey=$SigningKey",
    "DeviceCertificates__Password=$CertificatePassword",
    "DeviceCertificates__Directory=$(Join-Path $InstallPath 'certs')",
    "ConnectionStrings__KeyManagement=Data Source=$(Join-Path $InstallPath 'key-management.db')"
)

$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
New-ItemProperty -Path $key -Name 'Environment' -PropertyType MultiString -Value $environment -Force | Out-Null

Write-Host ''
Write-Host "Installed $Name."
Write-Host "  Binary:   $exe"
Write-Host "  Database: $(Join-Path $InstallPath 'key-management.db')"
Write-Host ''
Write-Host 'The database has no administrator until one is seeded. Set Seed__AdministratorPassword'
Write-Host 'in the service environment for the first start, then remove it.'
Write-Host ''
Write-Host "Start it with:  Start-Service $Name"
