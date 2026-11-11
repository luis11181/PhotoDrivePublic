#requires -Version 5.1
# PhotoDrive launcher for Windows (Docker Linux containers).
# Usage: .\photodrive.ps1 <command> [-Environment Production|Development] [options]
#
# Commands:
#   help           Show the usage summary.
#   configure      Save paths/port/etc. for this environment in .photodrive\<environment>.json, then print them.
#   config         Print the effective settings for this environment without changing anything.
#   build          Build the Docker image for this environment.
#   setup          First start on a computer: create folders, build, validate, apply migrations, start.
#   start          Start this computer's server (downloads its local database copy if missing); never migrates.
#   stop           Stop this computer's server; other computers keep working.
#   restart        Stop and start again without building or migrating.
#   update         Build the current code, validate, stop, apply migrations, start.
#   validate       Check host folders, AWS files, the image, and the app configuration; start nothing.
#   migrate        Apply database migrations to the shared Turso database only.
#   (import-sqlite is REMOVE-WITH-SQLITE-IMPORT.)
#   import-sqlite  Stop PhotoDrive, then copy the old SQLite catalog/Users files once into an empty Turso database.
#   logs           Follow the application logs.
#   status         Show this environment's containers.
#   health         Wait until the running server answers its /health check.
#
# Needs the untracked secrets.<Environment>.json next to appsettings.json (copy secrets.example.json).
[CmdletBinding()]
param(
    [ValidateSet('Production', 'Development')][string] $Environment = 'Production',
    [Parameter(Position = 0)][ValidateSet('help', 'configure', 'config', 'build', 'setup', 'start', 'stop', 'restart', 'update', 'validate', 'migrate', 'import-sqlite', 'logs', 'status', 'health')][string] $Command = 'help',
    [string] $DataDirectory, [string] $ImportDirectory, [string] $AwsDirectory,
    [int] $Port, [string] $BindAddress, [string] $ProjectName,
    [long] $OriginalCacheMaxBytes,
    [switch] $Detach
)
$ErrorActionPreference = 'Stop'
# Native Windows PowerShell 5.1 / PowerShell 7 deployment helpers.
# No command runs when this file is loaded; tests load these functions directly.
Set-StrictMode -Version Latest

function Get-PropertyValue($Object, [string] $Name, $Default = $null) {
    if ($null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]) { return $Object.$Name }
    return $Default
}

function Get-PhotoDriveConfiguration([string] $Root, [string] $Environment, [hashtable] $Overrides) {
    $homeDirectory = [Environment]::GetFolderPath('UserProfile')
    $production = $Environment -eq 'Production'
    $name = if ($production) { 'production' } else { 'development' }
    $dataRoot = Join-Path $homeDirectory 'PhotoDriveData'
    $settings = [ordered]@{
        DataDirectory = $dataRoot
        ImportDirectory = Join-Path $homeDirectory 'Downloads'
        AwsDirectory = Join-Path $homeDirectory '.aws'
        Port = $(if ($production) { 60020 } else { 80 })
        BindAddress = '127.0.0.1'
        OriginalCacheMaxBytes = 4294967296L
        ProjectName = $(if ($production) { 'photodrive-windows' } else { 'photodrive-development' })
    }
    $settingsPath = Join-Path $Root ".photodrive/$name.json"
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        foreach ($key in @($settings.Keys)) {
            if ($null -ne $saved.PSObject.Properties[$key]) { $settings[$key] = $saved.$key }
        }
    }
    foreach ($key in $Overrides.Keys) { $settings[$key] = $Overrides[$key] }
    foreach ($key in @('DataDirectory', 'ImportDirectory', 'AwsDirectory')) {
        if (-not [IO.Path]::IsPathRooted($settings[$key])) { throw "$key must be an absolute host path." }
        $settings[$key] = [IO.Path]::GetFullPath($settings[$key])
    }
    if ([int]$settings.Port -lt 1 -or [int]$settings.Port -gt 65535) { throw 'Port must be between 1 and 65535.' }
    $bindIp = $null
    if (-not [Net.IPAddress]::TryParse($settings.BindAddress, [ref]$bindIp) -or $bindIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'BindAddress must be an IPv4 address (127.0.0.1 for local access, 0.0.0.0 for all interfaces).'
    }
    if ($settings.ProjectName -notmatch '^[a-z0-9][a-z0-9_-]*$') { throw 'ProjectName must be a valid Docker Compose project name.' }
    if ($null -ne $settings.OriginalCacheMaxBytes -and [long]$settings.OriginalCacheMaxBytes -lt 0) { throw 'OriginalCacheMaxBytes cannot be negative.' }
    $leaf = if ($production) { 'MediaExplorer' } else { 'MediaExplorer-test' }
    return [pscustomobject]@{
        Root = $Root; Environment = $Environment; Production = $production
        Settings = $settings; SettingsPath = $settingsPath
        SecretsPath = Join-Path $Root "backend/main/DotnetBackEnd/solution/src/Hosts/CSSApps.Api/secrets.$Environment.json"
        DatabaseDirectory = Join-Path $settings.DataDirectory "Databases/$leaf"
        CacheDirectory = Join-Path $settings.DataDirectory "Caches/$leaf"
        ImportTarget = '/imports/windows'
        ComposeFile = $(if ($production) { 'compose.windows.yaml' } else { 'compose.development.yaml' })
        Image = $(if ($production) { 'photodrive-personal:local' } else { 'photodrive-development:local' })
    }
}

# Secrets (the Turso database token) live in the untracked secrets.<Environment>.json next to the other
# host settings; secrets.example.json is the tracked template.
function Assert-PhotoDriveSecrets($Config) {
    if (-not (Test-Path -LiteralPath $Config.SecretsPath -PathType Leaf)) {
        throw "Missing $($Config.SecretsPath). Copy secrets.example.json next to it, rename it, and add the Turso database token."
    }
}

function Set-PhotoDriveComposeEnvironment($Config) {
    $env:PHOTODRIVE_DATABASE_DIRECTORY = $Config.DatabaseDirectory.Replace('\', '/')
    $env:PHOTODRIVE_CACHE_DIRECTORY = $Config.CacheDirectory.Replace('\', '/')
    $env:PHOTODRIVE_AWS_DIRECTORY = $Config.Settings.AwsDirectory.Replace('\', '/')
    $env:PHOTODRIVE_IMPORT_DIRECTORY = $Config.Settings.ImportDirectory.Replace('\', '/')
    $env:PHOTODRIVE_IMPORT_TARGET = $Config.ImportTarget
    $env:PHOTODRIVE_PORT = [string]$Config.Settings.Port
    $env:PHOTODRIVE_BIND_ADDRESS = $Config.Settings.BindAddress
    $env:PHOTODRIVE_REVERSE_PROXY_ENABLED = 'false'
    $env:PHOTODRIVE_TMPFS_SIZE = '512m'
    $env:PHOTODRIVE_SHM_SIZE = '128m'
    $env:PHOTODRIVE_HOST_NAME = $env:COMPUTERNAME
    Assert-PhotoDriveSecrets $Config
    $env:PHOTODRIVE_SECRETS_FILE = $Config.SecretsPath.Replace('\', '/')
    $env:MediaStorage__LocalDisk__OriginalCacheMaxBytes = if ($null -ne $Config.Settings.OriginalCacheMaxBytes) { [string]$Config.Settings.OriginalCacheMaxBytes } else { $null }
}

function Invoke-PhotoDriveCompose($Config, [string[]] $Arguments) {
    & docker compose --project-directory $Config.Root --project-name $Config.Settings.ProjectName --file (Join-Path $Config.Root $Config.ComposeFile) @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed (exit $LASTEXITCODE)." }
}

function Initialize-PhotoDriveLayout($Config) {
    foreach ($directory in @($Config.DatabaseDirectory, $Config.CacheDirectory, $Config.Settings.ImportDirectory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

function Assert-PhotoDriveMounts($Config) {
    foreach ($directory in @($Config.DatabaseDirectory, $Config.CacheDirectory, $Config.Settings.ImportDirectory, $Config.Settings.AwsDirectory)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Missing host directory: $directory. Run setup or configure the correct paths." }
    }
    foreach ($name in @('config', 'credentials')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Config.Settings.AwsDirectory $name) -PathType Leaf)) {
            throw "Missing AWS $name file. Configure the environment's named AWS profile first."
        }
    }
}

function Wait-PhotoDriveHealthy($Config) {
    $url = "http://127.0.0.1:$($Config.Settings.Port)/health"
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        try {
            if ((Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) {
                Write-Host "PhotoDrive $($Config.Environment) is healthy at http://localhost:$($Config.Settings.Port)"
                return
            }
        } catch { }
        Start-Sleep -Seconds 1
    }
    throw 'PhotoDrive did not become healthy. Run logs to inspect the container.'
}

function Show-PhotoDriveHelp {
    Write-Host @"
PhotoDrive for Windows (Docker Linux containers)
  .\photodrive.ps1 <command> [-Environment Production|Development] [options]

Everyday commands:
  setup     build, apply database migrations, start
  start     start this computer's server (downloads its local database copy if missing)
  stop      stop this computer's server; other computers keep working
  update    build, apply database migrations, restart
  status    show running/stopped containers
  logs      follow application logs

The database lives in Turso Cloud. Put its token in the untracked secrets.<Environment>.json next to
appsettings.json (copy secrets.example.json). Choose the primary server in Developer tools.

Other: migrate, import-sqlite (one-time move of old SQLite files), build, validate,
  restart, health, config, configure. See docs/LAUNCHERS.md.

Settings: -DataDirectory, -ImportDirectory, -AwsDirectory, -Port, -BindAddress,
  -OriginalCacheMaxBytes, -ProjectName. Use configure to save them for this env. Dev: -Detach.
Production is the default; choose -Environment Development for test data.
"@
}

function Assert-Image {
    $null = & docker image inspect $script:config.Image 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'The environment image is missing. Run build or setup first.' }
}

function Invoke-Validation {
    Assert-PhotoDriveMounts $script:config
    Assert-Image
    Invoke-PhotoDriveCompose $script:config @('run', '--rm', '--no-deps', '-T', 'photodrive', 'config', 'validate')
}

function Invoke-Migrations {
    Invoke-PhotoDriveCompose $script:config @('run', '--rm', '--no-deps', '-T', 'photodrive', 'database', 'migrate', '--confirm', 'MIGRATE')
}

# REMOVE-WITH-SQLITE-IMPORT: part of the one-time v1 (SQLite) to v2 (Turso) import. Development was imported on 2026-11-09 and Production on 2026-11-10; delete it once the v1 files are no longer kept (search the repo for this marker).
# One-time copy of the previous two-file SQLite library (in the database directory) into an empty Turso database.
function Invoke-SqliteImport {
    foreach ($name in @('media-explorer.catalog.db', 'media-explorer.users.db')) {
        if (-not (Test-Path -LiteralPath (Join-Path $script:config.DatabaseDirectory $name) -PathType Leaf)) { throw "import-sqlite needs $name in $($script:config.DatabaseDirectory)." }
    }
    # The running (old) app must not keep writing to the files while they are copied.
    Invoke-PhotoDriveCompose $script:config @('stop')
    Invoke-PhotoDriveCompose $script:config @('run', '--rm', '--no-deps', '-T', 'photodrive', 'database', 'import-sqlite',
        '--catalog', '/data/databases/media-explorer.catalog.db', '--users', '/data/databases/media-explorer.users.db', '--confirm', 'IMPORT')
}

function Start-PhotoDrive {
    if ($script:config.Production -or $Detach) {
        Invoke-PhotoDriveCompose $script:config @('up', '--detach', '--no-build')
        Wait-PhotoDriveHealthy $script:config
    } else {
        Write-Host "Starting Development at http://localhost:$($script:config.Settings.Port). Ctrl+C stops it."
        Invoke-PhotoDriveCompose $script:config @('up', '--no-build', '--abort-on-container-exit')
    }
}

# Dot-sourcing loads helpers for the isolated verification script without running a command.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
    if ($Command -eq 'help') { Show-PhotoDriveHelp; exit 0 }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Use the native ./photodrive shell launcher on Arch/Linux.' }
    if ($Detach -and $Command -notin @('setup', 'start', 'restart', 'update')) { throw '-Detach applies only to setup/start/restart/update.' }
    $overrides = @{}
    foreach ($key in @('DataDirectory', 'ImportDirectory', 'AwsDirectory', 'Port', 'BindAddress', 'ProjectName', 'OriginalCacheMaxBytes')) {
        if ($PSBoundParameters.ContainsKey($key)) { $overrides[$key] = $PSBoundParameters[$key] }
    }
    $script:config = Get-PhotoDriveConfiguration ([IO.Path]::GetFullPath($PSScriptRoot)) $Environment $overrides
    if ($Command -eq 'configure') {
        New-Item -ItemType Directory -Path (Split-Path $config.SettingsPath -Parent) -Force | Out-Null
        $config.Settings | ConvertTo-Json | Set-Content -LiteralPath $config.SettingsPath -Encoding UTF8
        Write-Host "Saved $Environment settings in $($config.SettingsPath)."
    }
    if ($Command -in @('configure', 'config')) {
        [ordered]@{ Environment = $Environment; Settings = $config.Settings; Databases = $config.DatabaseDirectory; Cache = $config.CacheDirectory; ImportPathInContainer = $config.ImportTarget; Compose = $config.ComposeFile; Image = $config.Image } | ConvertTo-Json -Depth 4 | Write-Output
        exit 0
    }
    if ($Command -eq 'health') { Wait-PhotoDriveHealthy $config; exit 0 }
    $dockerOs = & docker info --format '{{.OSType}}' 2>&1
    if ($LASTEXITCODE -ne 0 -or ($dockerOs | Out-String).Trim() -ne 'linux') { throw 'Start Docker with Linux containers and Docker Compose v2, then retry.' }
    Set-PhotoDriveComposeEnvironment $config
    if ($Command -in @('stop', 'status', 'logs', 'build')) {
        $arguments = switch ($Command) { 'stop' { @('stop') }; 'status' { @('ps', '--all') }; 'logs' { @('logs', '--follow', '--tail', '100') }; 'build' { @('build') } }
        Invoke-PhotoDriveCompose $config $arguments
        exit 0
    }
    if ($Command -eq 'setup') { Initialize-PhotoDriveLayout $config }
    Assert-PhotoDriveMounts $config
    if ($Command -in @('setup', 'update')) { Invoke-PhotoDriveCompose $config @('build') }
    Invoke-Validation
    if ($Command -eq 'validate') { exit 0 }
    if ($Command -in @('update', 'restart')) { Invoke-PhotoDriveCompose $config @('stop') }
    if ($Command -eq 'import-sqlite') { Invoke-SqliteImport; exit 0 } # REMOVE-WITH-SQLITE-IMPORT
    if ($Command -in @('setup', 'update', 'migrate')) { Invoke-Migrations }
    if ($Command -eq 'migrate') { exit 0 }
    Start-PhotoDrive
} catch {
    [Console]::Error.WriteLine("PhotoDrive ${Environment}: $($_.Exception.Message)")
    exit 1
}
exit 0
