[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Program,
    [Parameter(Mandatory = $true)][string]$OriginalFixture,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('process-' + [guid]::NewGuid().ToString('N'))
$rootA = Join-Path $testRoot 'selected'
$rootB = Join-Path $testRoot 'unrelated'
$binA = Join-Path $rootA 'bin-1.0'
$binB = Join-Path $rootB 'bin-1.0'
New-Item -ItemType Directory -Path $binA, $binB -Force | Out-Null
$dll = Join-Path $binA 'NutstoreLib.dll'
Copy-Item -LiteralPath $OriginalFixture -Destination $dll
$source = Join-Path $testRoot 'Component.cs'
[IO.File]::WriteAllText($source, 'class Component { static void Main() { System.Threading.Thread.Sleep(300000); } }')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$workerA = Join-Path $binA 'Nutstore.TestComponent.exe'
$workerB = Join-Path $binB 'Nutstore.TestComponent.exe'
& $compiler /nologo /target:winexe "/out:$workerA" $source
if ($LASTEXITCODE -ne 0) { throw 'Cannot compile process fixture.' }
Copy-Item -LiteralPath $workerA -Destination $workerB
$selected = $null
$unrelated = $null
function Run-Tool([string]$Action, [bool]$Close, [int]$Expected) {
    $arguments = @($Action, ('"' + $rootA + '"'))
    if ($Close) { $arguments += '--close' }
    $name = $Action + '-' + $Close
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Program)) -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $testRoot ($name + '.stdout')) -RedirectStandardError (Join-Path $testRoot ($name + '.stderr'))
    if ($process.ExitCode -ne $Expected) {
        Get-Content -Encoding UTF8 -LiteralPath (Join-Path $testRoot ($name + '.stdout')), (Join-Path $testRoot ($name + '.stderr')) | Write-Output
        throw "Unexpected $Action exit code: $($process.ExitCode), expected $Expected. Evidence: $testRoot"
    }
}
try {
    $selected = Start-Process -FilePath $workerA -PassThru -WindowStyle Hidden
    $unrelated = Start-Process -FilePath $workerB -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 400
    $before = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    Run-Tool 'patch' $false 1
    $selected.Refresh()
    if ($selected.HasExited -or (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $before) { throw 'Without --close the component or DLL was modified.' }
    Write-Output 'PASS: patch without --close preserves running component and original DLL.'
    Run-Tool 'status' $true 0
    $selected.Refresh()
    if ($selected.HasExited) { throw 'Status must never stop a component.' }
    Write-Output 'PASS: status never stops a component, even with --close.'
    Run-Tool 'patch' $true 0
    $selected.Refresh()
    $unrelated.Refresh()
    if (-not $selected.HasExited) { throw 'Selected component is still running.' }
    if ($unrelated.HasExited) { throw 'Component from unrelated installation was stopped.' }
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -eq $before) { throw 'DLL was not patched.' }
    Write-Output 'PASS: --close stops only the selected installation and patches its DLL.'
    Run-Tool 'restore' $true 0
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $before) { throw 'Restore did not preserve original bytes.' }
    Write-Output 'PASS: restore with --close restores exact original bytes.'
} finally {
    foreach ($owned in @($selected, $unrelated)) {
        if ($null -eq $owned) { continue }
        $owned.Refresh()
        if (-not $owned.HasExited) { $owned.Kill(); $owned.WaitForExit(5000) | Out-Null }
        $owned.Dispose()
    }
}
Write-Output "PROCESS_EVIDENCE=$testRoot"
