[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$program = [IO.Path]::GetFullPath($Executable)
$stream = [IO.File]::OpenRead($program)
$reader = New-Object IO.BinaryReader($stream)
try {
    if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Missing DOS header.' }
    $stream.Position = 0x3c
    $stream.Position = $reader.ReadInt32()
    if ($reader.ReadUInt32() -ne 0x4550) { throw 'Missing PE header.' }
    $optionalHeader = $stream.Position + 20
    $stream.Position = $optionalHeader + 68
    if ($reader.ReadUInt16() -ne 2) { throw 'The application must use the Windows GUI subsystem, without a console window.' }
} finally { $reader.Dispose() }
Write-Output 'PASS: binary uses the Windows GUI subsystem.'

$process = Start-Process -FilePath $program -WorkingDirectory ([IO.Path]::GetDirectoryName($program)) -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForInputIdle(15000)) { throw 'GUI did not initialize its message loop within 15 seconds.' }
    $process.Refresh()
    if ($process.HasExited) { throw "GUI exited during startup: $($process.ExitCode)" }
    Write-Output 'PASS: no-argument GUI initializes and remains running.'
} finally {
    if (-not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) { $process.Kill(); $process.WaitForExit() }
    }
    $process.Dispose()
}
