$ErrorActionPreference = 'Stop'
$appPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'MikuDesktop.exe'
$reportDirectory = Join-Path $env:TEMP ('MikuDesktop-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportDirectory | Out-Null
foreach ($mode in @('self-test','verify','benchmark')) {
    $reportPath = Join-Path $reportDirectory ($mode + '.txt')
    $testProcess = Start-Process -FilePath $appPath -ArgumentList @("--$mode", ('"' + $reportPath + '"')) -WindowStyle Hidden -PassThru
    if (-not $testProcess.WaitForExit(30000)) { throw "Test timed out: $mode. Test process id: $($testProcess.Id)" }
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Close the running Miku Desktop app before testing.' }
    $result = Get-Content -LiteralPath $reportPath -Raw
    Write-Output $result
    if ($testProcess.ExitCode -ne 0 -or $result.Contains('FAIL')) { throw "Test failed: $mode" }
}
Write-Output "Reports: $reportDirectory"
