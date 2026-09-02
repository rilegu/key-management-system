#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the whole system against a throwaway database.

.DESCRIPTION
    Builds, creates a demonstration database under demo/, issues the seeded cabinet its
    certificate, then starts the server, the cabinet simulator and the desktop client.

    Everything it creates lives under demo/ and certs/, both of which are ignored by git. The
    secrets are set as environment variables for the processes this script starts, so nothing is
    written to the machine and nothing survives the run.

.PARAMETER Reset
    Delete the demonstration database first, so the run starts from seeded data.

.PARAMETER NoClient
    Start the server and the simulator but not the desktop client.

.EXAMPLE
    powershell -File scripts/demo.ps1 -Reset
#>
[CmdletBinding()]
param(
    [switch]$Reset,
    [switch]$NoClient
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$demo = Join-Path $root 'demo'
$certs = Join-Path $demo 'certs'
$database = Join-Path $demo 'key-management.db'

# Fixed and obviously fake. These are demonstration credentials in a script anyone can read;
# treating them as though they were secret would be theatre. A deployment sets its own.
$env:Jwt__SigningKey = 'demonstration-signing-key-not-for-any-real-deployment'
$env:DeviceCertificates__Password = 'demonstration-certificate-password'
$env:DeviceCertificates__Directory = $certs
$env:Seed__AdministratorPassword = 'correct horse battery staple'
$env:Seed__AdministratorPin = '4821'
$env:ConnectionStrings__KeyManagement = "Data Source=$database"
$env:DeviceGateway__Enabled = 'true'
$env:DeviceGateway__Port = '5610'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5140'

# Development logging prints every statement, which buries the demo's own output under several
# hundred lines of SQL. The category has dots in it, so it cannot go through the $env: drive.
[Environment]::SetEnvironmentVariable(
    'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command', 'Warning', 'Process')

$server = Join-Path $root 'src\KeyManagement.Server'
$simulator = Join-Path $root 'src\KeyManagement.DeviceSimulator'
$client = Join-Path $root 'src\KeyManagement.Desktop'

$started = @()

function Stop-Everything {
    foreach ($process in $started) {
        if ($process -and -not $process.HasExited) {
            Write-Host "Stopping $($process.ProcessName)..."

            # Stop-Process rather than Process.Kill(true): the overload that kills a process
            # tree does not exist under Windows PowerShell 5.1, so calling it would throw here,
            # in the one place that has to work.
            try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
        }
    }

    # dotnet run launches the application as a child, so stopping the launcher can leave the
    # real process behind holding the database and the port.
    Get-Process -Name 'KeyManagement.Server', 'KeyManagement.DeviceSimulator', 'KeyManagement.Desktop' `
        -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

try {
    if ($Reset -and (Test-Path $demo)) {
        Write-Host 'Removing the previous demonstration data...'
        Remove-Item $demo -Recurse -Force
    }

    New-Item -ItemType Directory -Path $certs -Force | Out-Null

    Write-Host 'Building...'
    & dotnet build $root --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

    # Creates the database, seeds it, and issues the certificate the cabinet needs. Runs and
    # exits, so the server is not left holding the file.
    Write-Host 'Preparing the database and enrolling the cabinet...'
    & dotnet run --project $server --no-build --no-launch-profile -- --issue-cabinet-certificate Reception
    if ($LASTEXITCODE -ne 0) { throw 'Could not enrol the cabinet.' }

    Write-Host 'Starting the server...'
    $started += Start-Process -FilePath 'dotnet' -PassThru `
        -ArgumentList @('run', '--project', $server, '--no-build', '--no-launch-profile')

    # The simulator refuses to start if the gateway is not listening yet, so wait for the health
    # probe rather than guessing at a delay.
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 400
        $up = $false
        try {
            $up = (Invoke-WebRequest -Uri 'http://localhost:5140/health' -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200
        } catch { }
    } while (-not $up -and (Get-Date) -lt $deadline)

    if (-not $up) { throw 'The server did not become ready.' }
    Write-Host 'Server is up on http://localhost:5140'

    $config = Join-Path $demo 'simulator.json'
    @{
        host = '127.0.0.1'
        port = 5610
        serverName = 'localhost'
        certificatePassword = $env:DeviceCertificates__Password
        authorityPath = (Join-Path $certs 'device-authority.pfx')
        cabinets = @(
            @{
                name = 'Reception'
                certificatePath = (Join-Path $certs 'cabinet-reception.pfx')
                firmwareVersion = '1.4.2'
                positions = @(1..10 | ForEach-Object {
                    @{ position = ('A{0:D2}' -f $_); occupied = ($_ -le 5) }
                })
            }
        )
    } | ConvertTo-Json -Depth 6 | Set-Content -Path $config -Encoding utf8

    Write-Host 'Starting the cabinet simulator...'
    $started += Start-Process -FilePath 'dotnet' -PassThru `
        -ArgumentList @('run', '--project', $simulator, '--no-build', '--no-launch-profile', '--', '--config', $config)

    if (-not $NoClient) {
        Write-Host 'Starting the desktop client...'
        $started += Start-Process -FilePath 'dotnet' -PassThru `
            -ArgumentList @('run', '--project', $client, '--no-build', '--no-launch-profile', '--', '--server', 'http://localhost:5140')
    }

    Write-Host ''
    Write-Host 'Running. Sign in with:'
    Write-Host '    admin / correct horse battery staple      (cabinet PIN 4821)'
    Write-Host ''
    Write-Host 'Try, in the simulator window:'
    Write-Host '    take A01        after releasing PR-001 in the client'
    Write-Host '    drop            the cabinet goes offline, its positions become unconfirmed'
    Write-Host '    take A02        moved while nobody is watching'
    Write-Host '    attach          the buffered event replays and custody reconciles'
    Write-Host '    pin admin 4821 A03    a request typed at the cabinet keypad'
    Write-Host ''
    Write-Host 'Press Enter to stop everything.'
    [void](Read-Host)
}
finally {
    Stop-Everything
}
