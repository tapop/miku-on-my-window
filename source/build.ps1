param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourcePath = $PSScriptRoot
$destinationPath = Split-Path $sourcePath -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $destinationPath 'MikuDesktop.exe' }
$atlasPath = Join-Path $sourcePath 'spritesheet.png'
if (-not (Test-Path -LiteralPath $atlasPath)) {
    throw 'Place spritesheet.png in the source directory before building.'
}
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /utf8output "/out:$OutputPath" "/win32manifest:$sourcePath\app.manifest" "/win32icon:$sourcePath\miku.ico" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/resource:$atlasPath,MikuSprites" "/resource:$sourcePath\miku.ico,MikuIcon" "$sourcePath\MikuDesktop.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
