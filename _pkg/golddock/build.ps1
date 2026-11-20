$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$out  = Join-Path $dist 'GoldDock.exe'
$files = Get-ChildItem -Path $src -Filter *.cs | ForEach-Object { $_.FullName }

Write-Host "Compiling $($files.Count) source files -> $out"

$args = @(
  '/nologo',
  '/target:winexe',
  '/platform:x64',
  '/optimize+',
  '/warn:4',
  "/out:$out",
  '/r:System.dll',
  '/r:System.Core.dll',
  '/r:System.Drawing.dll',
  '/r:System.Windows.Forms.dll'
) + $files

& $csc $args
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$fi = Get-Item $out
Write-Host ("OK  {0}  {1:N0} bytes  {2}" -f $fi.Name, $fi.Length, $fi.LastWriteTime)
