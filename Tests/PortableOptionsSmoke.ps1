[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Program,
    [Parameter(Mandatory = $true)][string]$OriginalLib,
    [Parameter(Mandatory = $true)][string]$OriginalLegacy,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('combo-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
function New-Installation([string]$Name) {
    $installation = Join-Path $root $Name
    $bin = Join-Path $installation 'bin-1.0'
    New-Item -ItemType Directory -Path $bin -Force | Out-Null
    Copy-Item -LiteralPath $OriginalLib -Destination (Join-Path $bin 'NutstoreLib.dll')
    Copy-Item -LiteralPath $OriginalLegacy -Destination (Join-Path $bin 'NutstoreClient.Legacy.dll')
    [IO.File]::WriteAllText((Join-Path $bin 'NutstoreClient.exe.config'), '<configuration><appSettings><add key="keep" value="unchanged" /></appSettings><log4net><appender><file value="${APPDATA}\Nutstore\logs\client.log" /></appender></log4net></configuration>')
    return $installation
}
function Run-Option([string]$Name, [string[]]$Arguments, [int]$Expected) {
    $stdout = Join-Path $root ($Name + '.stdout')
    $stderr = Join-Path $root ($Name + '.stderr')
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Program)) -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        if ($process.ExitCode -ne $Expected) {
            Get-Content -Encoding UTF8 -LiteralPath $stdout, $stderr | Write-Output
            throw "Option integration '$Name' returned $($process.ExitCode), expected $Expected."
        }
    } finally { $process.Dispose() }
}
function Hashes([string]$Installation) {
    $bin = Join-Path $Installation 'bin-1.0'
    return @('NutstoreLib.dll','NutstoreClient.Legacy.dll','NutstoreClient.exe.config') | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $bin $_) -Algorithm SHA256).Hash }
}
$installation = New-Installation 'normal'
$bin = Join-Path $installation 'bin-1.0'
$quoted = '"' + $installation + '"'
$before = @(Hashes $installation)
$flags = @('--no-userdata', '--disable-background-updates', '--portable-logs')
Run-Option 'status' (@('status', $quoted) + $flags) 0
if ((@(Hashes $installation) -join ',') -ne ($before -join ',')) { throw 'Status changed option inputs.' }
Run-Option 'apply' (@('patch', $quoted) + $flags) 0
$after = @(Hashes $installation)
if ($after[0] -ne $before[0] -or $after[1] -eq $before[1] -or $after[2] -eq $before[2]) { throw 'Selected options did not change exactly their inputs.' }
[xml]$xml = Get-Content -Raw -LiteralPath (Join-Path $bin 'NutstoreClient.exe.config')
if ($xml.configuration.log4net.appender.file.value -ne '..\UserData\logs\client.log') { throw 'CLI did not make logs portable.' }
Run-Option 'repeat' (@('--portable-logs', 'patch', '--disable-background-updates', $quoted, '--no-userdata')) 0
if ((@(Hashes $installation) -join ',') -ne ($after -join ',')) { throw 'Repeated options changed bytes.' }
Run-Option 'restore' (@('restore', $quoted) + $flags) 0
if ((@(Hashes $installation) -join ',') -ne ($before -join ',')) { throw 'Combined restore was not byte-identical.' }
Write-Output 'PASS: CLI combined options preserve unselected UserData and restore selected files exactly.'

$missing = New-Installation 'missing-component'
Remove-Item -LiteralPath (Join-Path $missing 'bin-1.0/NutstoreClient.Legacy.dll')
$beforeMissing = (Get-FileHash -LiteralPath (Join-Path $missing 'bin-1.0/NutstoreLib.dll')).Hash
Run-Option 'missing-component' @('patch', ('"' + $missing + '"'), '--disable-background-updates') 1
if ((Get-FileHash -LiteralPath (Join-Path $missing 'bin-1.0/NutstoreLib.dll')).Hash -ne $beforeMissing) { throw 'Missing optional component allowed partial modification before preflight.' }
Write-Output 'PASS: unsupported optional component is rejected before modifying UserData.'

$unbacked = New-Installation 'no-backup'
$unbackedBin = Join-Path $unbacked 'bin-1.0'
Run-Option 'no-backup' (@('patch', ('"' + $unbacked + '"')) + $flags + '--no-backup') 0
if (@(Get-ChildItem -LiteralPath $unbackedBin -File | Where-Object { $_.Name -like '*.bak' -or $_.Name -like '*.state' }).Count -ne 0) { throw 'Disabled backup created option records.' }
$unbackedHashes = @(Hashes $unbacked)
Run-Option 'no-backup-restore' (@('restore', ('"' + $unbacked + '"')) + $flags) 1
if ((@(Hashes $unbacked) -join ',') -ne ($unbackedHashes -join ',')) { throw 'Rejected restore modified unbacked inputs.' }
Write-Output 'PASS: combined no-backup creates no backups and rejects unsafe restoration.'

Run-Option 'empty-selection' @('patch', $quoted, '--no-userdata') 1
Run-Option 'duplicate-flag' @('status', $quoted, '--portable-logs', '--portable-logs') 1
Run-Option 'restore-no-backup' @('restore', $quoted, '--no-backup') 1
Write-Output 'PASS: invalid selections and flags are rejected.'
Write-Output "OPTION_INTEGRATION_EVIDENCE=$root"
