$project = Join-Path $PSScriptRoot '../tests/ApiAggregation.SmokeTests/ApiAggregation.SmokeTests.csproj'
$previousOptIn = [Environment]::GetEnvironmentVariable('RUN_LIVE_SMOKE_TESTS', 'Process')

Push-Location (Join-Path $PSScriptRoot '..')
try {
    [Environment]::SetEnvironmentVariable('RUN_LIVE_SMOKE_TESTS', '1', 'Process')
    dotnet test $project -c Release --logger 'console;verbosity=normal'
    $testExitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable('RUN_LIVE_SMOKE_TESTS', $previousOptIn, 'Process')
    Pop-Location
}

exit $testExitCode
