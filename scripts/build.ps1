[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$Version = 'local',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath($OutputRoot)
$buildPath = Join-Path $outputPath ('build-' + [guid]::NewGuid().ToString('N'))
$stagePath = Join-Path $outputPath ('package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildPath, $stagePath -Force | Out-Null

$msbuild = Get-Command msbuild.exe -ErrorAction SilentlyContinue
if ($msbuild) {
    $msbuildOutput = $buildPath.Replace('\', '/') + '/'
    & $msbuild.Source (Join-Path $repoRoot 'NutstoreAutoPatch.csproj') /nologo /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:TreatWarningsAsErrors=true "/p:OutputPath=$msbuildOutput"
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed: $LASTEXITCODE" }
} else {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'MSBuild or the .NET Framework C# compiler is required.' }
    Write-Warning 'MSBuild is unavailable; using the installed Framework compiler. CI uses MSBuild with the .NET Framework 4.8 targeting pack.'
    [xml]$project = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'NutstoreAutoPatch.csproj')
    $ns = New-Object Xml.XmlNamespaceManager($project.NameTable)
    $ns.AddNamespace('p', 'http://schemas.microsoft.com/developer/msbuild/2003')
    $sources = @($project.SelectNodes('//p:Compile', $ns) | ForEach-Object { Join-Path $repoRoot $_.Include })
    $dependencyFiles = @($project.SelectNodes('//p:Reference/p:HintPath', $ns) | ForEach-Object { Join-Path $repoRoot $_.InnerText })
    $references = @($dependencyFiles)
    $references += @($project.SelectNodes('//p:Reference[not(p:HintPath)]', $ns) | ForEach-Object { ($_.Include -split ',')[0] + '.dll' })
    $outputType = $project.SelectSingleNode('//p:OutputType', $ns).InnerText
    $target = switch ($outputType) { 'WinExe' { 'winexe' }; 'Exe' { 'exe' }; default { throw "Unsupported project OutputType: $outputType" } }
    $arguments = @('/nologo', '/optimize+', '/warn:4', '/warnaserror+', "/target:$target", "/out:$(Join-Path $buildPath 'NutstoreAutoPatch.exe')")
    $arguments += @($references | ForEach-Object { '/reference:' + $_ })
    & $compiler @arguments @sources
    if ($LASTEXITCODE -ne 0) { throw "C# compilation failed: $LASTEXITCODE" }
    foreach ($dependencyFile in $dependencyFiles) { Copy-Item -LiteralPath $dependencyFile -Destination $buildPath }
    $config = Join-Path $repoRoot 'App.config'
    if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination (Join-Path $buildPath 'NutstoreAutoPatch.exe.config') }
}

$executable = Join-Path $buildPath 'NutstoreAutoPatch.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build did not produce NutstoreAutoPatch.exe.' }
Copy-Item -LiteralPath $executable -Destination $stagePath
Get-ChildItem -LiteralPath $buildPath -File -Filter '*.dll' | Copy-Item -Destination $stagePath
$configFile = Join-Path $buildPath 'NutstoreAutoPatch.exe.config'
if (Test-Path -LiteralPath $configFile) { Copy-Item -LiteralPath $configFile -Destination $stagePath }
foreach ($name in @('README.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $stagePath }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $stagePath
& (Join-Path $PSScriptRoot 'test.ps1') -BinaryDirectory $buildPath -OutputRoot $outputPath

$zipPath = Join-Path $outputPath "NutstoreAutoPatch-$Version-win-anycpu.zip"
Compress-Archive -Path (Join-Path $stagePath '*') -DestinationPath $zipPath -Force
$digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $outputPath 'SHA256SUMS.txt'
[IO.File]::WriteAllText($checksumPath, "$digest  $([IO.Path]::GetFileName($zipPath))`n", [Text.Encoding]::ASCII)
Write-Output "BUILD_DIRECTORY=$buildPath"
Write-Output "PACKAGE=$zipPath"
Write-Output "CHECKSUMS=$checksumPath"
