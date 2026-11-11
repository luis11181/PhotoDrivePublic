# Tests use synthetic paths and mock Docker only. No AWS, Turso, or live database access.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../photodrive.ps1"
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$temporary = Join-Path $root ('.photodrive/launcher-check-' + [guid]::NewGuid().ToString('N'))
$passed = 0
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message }; $script:passed++ }
function Fails([scriptblock] $Action, [string] $Pattern) {
    $old = [Console]::Error; $capture = [IO.StringWriter]::new()
    try { [Console]::SetError($capture); & $Action; Assert ($LASTEXITCODE -eq 1 -and $capture.ToString() -match $Pattern) "Expected '$Pattern', got '$capture'" }
    finally { [Console]::SetError($old); $capture.Dispose() }
}
$global:PhotoDriveTestCalls = [Collections.Generic.List[string]]::new()
function global:docker {
    $global:PhotoDriveTestCalls.Add(($args -join ' ')); $global:LASTEXITCODE = 0
    if ($args[0] -eq 'info') { 'linux' }
}
$createdSecrets = $false
try {
    $data = Join-Path $temporary 'data with spaces'; $imports = Join-Path $temporary 'imports'; $aws = Join-Path $temporary 'aws'
    New-Item -ItemType Directory -Path $aws -Force | Out-Null
    Set-Content (Join-Path $aws 'config') ''; Set-Content (Join-Path $aws 'credentials') ''
    $prod = Get-PhotoDriveConfiguration $root 'Production' @{ DataDirectory = $data }
    $dev = Get-PhotoDriveConfiguration $root 'Development' @{ DataDirectory = $data }
    Assert ($prod.DatabaseDirectory -ne $dev.DatabaseDirectory) 'Dev/Production replicas must be separate'
    Assert ($prod.CacheDirectory -ne $dev.CacheDirectory) 'Dev/Production caches must be separate'
    Assert ($prod.SecretsPath -ne $dev.SecretsPath) 'Dev/Production secrets must be separate'

    if (-not (Test-Path -LiteralPath $dev.SecretsPath)) {
        Fails { & "$root/photodrive.ps1" start -Environment Development -DataDirectory $data -ImportDirectory $imports -AwsDirectory $aws } 'Missing .*secrets'
        Assert (-not ($global:PhotoDriveTestCalls | Where-Object { $_ -match ' run | up ' })) 'Nothing may start without a secrets file'
        Set-Content -LiteralPath $dev.SecretsPath '{ "Database": { "AuthToken": "test-token" } }'
        $createdSecrets = $true
    }
    New-Item -ItemType Directory -Path $dev.DatabaseDirectory, $dev.CacheDirectory, $imports -Force | Out-Null
    # REMOVE-WITH-SQLITE-IMPORT: part of the one-time v1 (SQLite) to v2 (Turso) import. Development was imported on 2026-11-09 and Production on 2026-11-10; delete it once the v1 files are no longer kept (search the repo for this marker).
    Fails { & "$root/photodrive.ps1" import-sqlite -Environment Development -DataDirectory $data -ImportDirectory $imports -AwsDirectory $aws } 'import-sqlite needs'
    Assert (-not ($global:PhotoDriveTestCalls | Where-Object { $_ -match ' stop$' })) 'A failed import-sqlite check must not stop the running app'
    foreach ($name in @('media-explorer.catalog.db', 'media-explorer.users.db')) { Set-Content -LiteralPath (Join-Path $dev.DatabaseDirectory $name) '' }
    & "$root/photodrive.ps1" import-sqlite -Environment Development -DataDirectory $data -ImportDirectory $imports -AwsDirectory $aws
    Assert ($LASTEXITCODE -eq 0) 'import-sqlite should succeed through Compose'
    $importCalls = @($global:PhotoDriveTestCalls | Where-Object { $_ -match ' stop$| database import-sqlite ' })
    Assert ($importCalls.Count -eq 2 -and $importCalls[0] -match ' stop$') 'import-sqlite must stop the running app before importing'
    & "$root/photodrive.ps1" migrate -Environment Development -DataDirectory $data -ImportDirectory $imports -AwsDirectory $aws
    Assert ($LASTEXITCODE -eq 0) 'Migrate should succeed through Compose'
    Assert ([bool]($global:PhotoDriveTestCalls | Where-Object { $_ -match 'photodrive-development.*database migrate --confirm MIGRATE' })) 'Migrate must use the Development Compose project'
    Assert ($env:PHOTODRIVE_SECRETS_FILE -eq $dev.SecretsPath.Replace('\', '/') -and $env:PHOTODRIVE_HOST_NAME -eq $env:COMPUTERNAME) 'Compose must receive the secrets file and host name'
    Write-Output "Windows launcher checks passed: $passed"
} finally {
    if ($createdSecrets) { Remove-Item -LiteralPath $dev.SecretsPath -Force }
    Remove-Item Function:/docker -ErrorAction SilentlyContinue
    Remove-Variable PhotoDriveTestCalls -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($temporary)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root '.photodrive')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::Ordinal)) { throw 'Unexpected cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
exit 0
