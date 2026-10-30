param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot ('builds/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$target = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Output directory already exists. Choose a new directory to preserve previous files.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler is unavailable.' }
New-Item -ItemType Directory -Path $target | Out-Null
$assetSource = Join-Path $projectRoot 'assets'
$iconSource = Join-Path $projectRoot 'pet.ico'
if (-not (Test-Path -LiteralPath $assetSource)) { $assetSource = Join-Path (Split-Path -Parent $projectRoot) 'assets' }
if (-not (Test-Path -LiteralPath $iconSource)) { $iconSource = Join-Path (Split-Path -Parent $projectRoot) 'pet.ico' }
Copy-Item -LiteralPath $assetSource -Destination $target -Recurse
Copy-Item -LiteralPath $iconSource -Destination $target
$exePath = Join-Path $target '豹嗷喵.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /utf8output "/out:$exePath" "/win32icon:$iconSource" "/win32manifest:$projectRoot/src/app.manifest" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll (Join-Path $projectRoot 'src/PetApp.cs')
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $projectRoot '使用说明.md') -Destination $target
Write-Output $exePath
