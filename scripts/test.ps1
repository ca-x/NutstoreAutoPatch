[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BinaryDirectory,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binaryPath = [IO.Path]::GetFullPath($BinaryDirectory)
$testPath = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testPath -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler is required to run tests.' }
$program = Join-Path $binaryPath 'NutstoreAutoPatch.exe'
$cecil = Join-Path $binaryPath 'Mono.Cecil.dll'
Copy-Item -LiteralPath $program -Destination $testPath
Get-ChildItem -LiteralPath $binaryPath -File -Filter '*.dll' | Copy-Item -Destination $testPath
$testExe = Join-Path $testPath 'RegressionTests.exe'
& $compiler /nologo /warn:4 /warnaserror+ "/out:$testExe" "/reference:$program" "/reference:$cecil" (Join-Path $repoRoot 'Tests\RegressionTests.cs')
if ($LASTEXITCODE -ne 0) { throw "Regression test compilation failed: $LASTEXITCODE" }
$probeExe = Join-Path $testPath 'PathProbe.exe'
& $compiler /nologo /warn:4 /warnaserror+ "/out:$probeExe" (Join-Path $repoRoot 'Tests\PathProbe.cs')
if ($LASTEXITCODE -ne 0) { throw "Path probe compilation failed: $LASTEXITCODE" }
$fixtures = Join-Path $testPath 'fixtures'
& $testExe $fixtures
if ($LASTEXITCODE -ne 0) { throw "Regression tests failed: $LASTEXITCODE" }
& $probeExe (Join-Path $fixtures 'move-a\bin-1.0\NutstoreLib.dll')
if ($LASTEXITCODE -ne 0) { throw "Initial relative path check failed: $LASTEXITCODE" }
[IO.Directory]::Move((Join-Path $fixtures 'move-a'), (Join-Path $fixtures 'move-b'))
& $probeExe (Join-Path $fixtures 'move-b\bin-1.0\NutstoreLib.dll')
if ($LASTEXITCODE -ne 0) { throw "Relocated relative path check failed: $LASTEXITCODE" }
Write-Output 'PASS: relative data path follows the relocated application directory.'

foreach ($testName in @('UpdateOptionTests', 'ConfigOptionTests')) {
    $optionExe = Join-Path $testPath ($testName + '.exe')
    & $compiler /nologo /warn:4 /warnaserror+ "/out:$optionExe" "/reference:$program" "/reference:$cecil" /reference:System.Xml.dll (Join-Path $repoRoot ('Tests\' + $testName + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "$testName compilation failed: $LASTEXITCODE" }
    $optionRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('option-' + [guid]::NewGuid().ToString('N'))
    & $optionExe $optionRoot
    if ($LASTEXITCODE -ne 0) { throw "$testName failed: $LASTEXITCODE" }
    if ($testName -eq 'UpdateOptionTests') { $updateFixtures = $optionRoot }
}

function Invoke-ToolCheck {
    param([string]$Action, [string]$Path, [int]$ExpectedExitCode)
    $runId = [guid]::NewGuid().ToString('N')
    $stdout = Join-Path $testPath ($runId + '.stdout.txt')
    $stderr = Join-Path $testPath ($runId + '.stderr.txt')
    $process = Start-Process -FilePath $program -ArgumentList @($Action, ('"' + $Path + '"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        if ($process.ExitCode -ne $ExpectedExitCode) {
            Get-Content -LiteralPath $stdout, $stderr | Write-Output
            throw "CLI action '$Action' returned $($process.ExitCode), expected $ExpectedExitCode."
        }
    } finally { $process.Dispose() }
}

$relocated = Join-Path $fixtures 'move-b\bin-1.0\NutstoreLib.dll'
$beforeStatus = (Get-FileHash -LiteralPath $relocated -Algorithm SHA256).Hash
Invoke-ToolCheck -Action status -Path $relocated -ExpectedExitCode 0
Invoke-ToolCheck -Action invalid-action -Path $relocated -ExpectedExitCode 1
if ((Get-FileHash -LiteralPath $relocated -Algorithm SHA256).Hash -ne $beforeStatus) { throw 'Read-only CLI checks modified the assembly.' }
Write-Output 'PASS: WinExe CLI waits, returns exit codes, and preserves the input assembly.'
$originalFixture = @(Get-ChildItem -LiteralPath (Join-Path $fixtures 'normal\bin-1.0') -Filter '*.original.*.bak' -File)
if ($originalFixture.Count -ne 1) { throw 'Expected exactly one original fixture backup for process-control tests.' }
& (Join-Path $repoRoot 'Tests\PortableOptionsSmoke.ps1') -Program $program -OriginalLib $originalFixture[0].FullName -OriginalLegacy (Join-Path $updateFixtures 'cli-fixture\NutstoreClient.Legacy.dll') -OutputRoot $OutputRoot
& (Join-Path $repoRoot 'Tests\ProcessControlSmoke.ps1') -Program $program -OriginalFixture $originalFixture[0].FullName -OutputRoot $OutputRoot
& (Join-Path $PSScriptRoot 'gui-smoke.ps1') -Executable $program
Write-Output "TEST_EVIDENCE=$testPath"
