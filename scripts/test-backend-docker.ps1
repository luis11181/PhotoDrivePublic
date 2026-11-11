$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionRoot = Join-Path $repositoryRoot 'backend/main/DotnetBackEnd/solution'
$dockerDirectory = Join-Path $repositoryRoot 'docker'
& docker build -f (Join-Path $dockerDirectory 'Dockerfile.tests') -t photodrive-tests:local $dockerDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Named volumes keep NuGet packages and build outputs between runs: only the first run restores everything and
# compiles every project. Remove them (docker volume rm photodrive-tests-nuget photodrive-tests-work) to start clean.
& docker run --rm --mount "type=bind,source=$solutionRoot,target=/source,readonly" --mount "type=volume,source=photodrive-tests-nuget,target=/root/.nuget/packages" --mount "type=volume,source=photodrive-tests-work,target=/work" photodrive-tests:local
exit $LASTEXITCODE
