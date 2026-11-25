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
    $stream.Position = $optionalHeader
    $magic = $reader.ReadUInt16()
    $directoriesOffset = if ($magic -eq 0x10b) { 96 } elseif ($magic -eq 0x20b) { 112 } else { throw 'Unknown optional header format.' }
    $stream.Position = $optionalHeader + $directoriesOffset + 14 * 8
    $cliRva = $reader.ReadUInt32()
    $stream.Position = $optionalHeader - 18
    $sectionCount = $reader.ReadUInt16()
    $stream.Position = $optionalHeader - 4
    $optionalSize = $reader.ReadUInt16()
    $cliOffset = $null
    for ($section = 0; $section -lt $sectionCount; $section++) {
        $stream.Position = $optionalHeader + $optionalSize + 40 * $section + 8
        $virtualSize = $reader.ReadUInt32()
        $virtualAddress = $reader.ReadUInt32()
        $rawSize = $reader.ReadUInt32()
        $rawOffset = $reader.ReadUInt32()
        if ($cliRva -ge $virtualAddress -and $cliRva -lt $virtualAddress + [Math]::Max($virtualSize, $rawSize)) {
            $cliOffset = $rawOffset + $cliRva - $virtualAddress
            break
        }
    }
    if ($null -eq $cliOffset) { throw 'Managed CLI header not found.' }
    $stream.Position = $cliOffset + 16
    if (($reader.ReadUInt32() -band 0x20002) -ne 0) { throw 'The application must not prefer or require 32-bit mode; process inspection needs the native OS architecture.' }
} finally { $reader.Dispose() }
Write-Output 'PASS: binary uses the Windows GUI subsystem.'
Write-Output 'PASS: binary can inspect native-architecture processes without preferring 32-bit mode.'

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
