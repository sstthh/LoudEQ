# Build LoudEQ.exe using the built-in .NET Framework 4.8 compiler (no SDK required).
# NOTE: keep this file ASCII-only; PowerShell 5.1 reads BOM-less scripts as ANSI.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$fwDir = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fwDir 'csc.exe'
if (-not (Test-Path $csc)) { $fwDir = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"; $csc = Join-Path $fwDir 'csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe (NET Framework 4.x) not found" }

function Resolve-Ref([string]$name) {
    $p = Join-Path $fwDir $name
    if (Test-Path $p) { return $p }
    $hit = Get-ChildItem "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\$($name -replace '\.dll$','')\*\${name}" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { return $hit.FullName }
    throw "reference assembly not found: $name"
}

$refs = @('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll') |
    ForEach-Object { "`"/r:$(Resolve-Ref $_)`"" }

$src = Join-Path $root 'src\*.cs'
$out = Join-Path $root 'LoudEQ.exe'
$man = Join-Path $root 'app.manifest'

# Sources are UTF-8 with BOM (csc auto-detects them).
$cmd = "& `"$csc`" /nologo /optimize+ /target:winexe /platform:anycpu /win32manifest:`"$man`" /out:`"$out`" $refs `"$src`""
Write-Host $cmd
Invoke-Expression $cmd
if ($LASTEXITCODE -ne 0) { throw "build failed, exit code $LASTEXITCODE" }
Write-Host ("OK -> " + $out)
