# Compila MajinDesktop con el Roslyn del SDK de .NET contra .NET Framework 4.8 (ya incluido en Windows),
# así el .exe pesa unos KB y no necesita instalar ningún runtime.
# Uso:  pwsh -File build.ps1 [-Version 0.1.0]
#   dist\MajinDesktop.exe                    la app (portable)
#   dist\MajinDesktop-Setup-<versión>.exe    instalador con la app dentro
param([string]$Version = '0.2.0')
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\sdk') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'Roslyn\bincore\csc.dll' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'No encuentro el compilador Roslyn (instala el SDK de .NET 8 o superior).' }

$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $build, $dist | Out-Null
Get-ChildItem $dist -Filter 'MajinDesktop-Setup-*.exe' | Remove-Item

# Versión y datos que Windows enseña en Propiedades del .exe.
$info = Join-Path $build 'AssemblyInfo.g.cs'
@"
using System.Reflection;
[assembly: AssemblyTitle("MajinDesktop")]
[assembly: AssemblyProduct("MajinDesktop")]
[assembly: AssemblyCompany("Majin")]
[assembly: AssemblyCopyright("Majin")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content $info -Encoding utf8

$src = Join-Path $root 'src'
$appSources = @(Get-ChildItem $src -Filter *.cs | ForEach-Object FullName) + $info
# El instalador reutiliza el tema, los controles y lo mínimo que ellos necesitan.
$setupSources = @('Native', 'Theme', 'Controls', 'Displays', 'Config', 'Log' | ForEach-Object { Join-Path $src "$_.cs" }) +
    (Join-Path $root 'setup\Setup.cs') + $info
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Runtime.Serialization', 'System.Xml' |
    ForEach-Object { "/r:" + (Join-Path $fw "$_.dll") }

function Invoke-Csc([string]$out, [string[]]$sources, [string[]]$extra, [string]$target = 'winexe') {
    $cscArgs = @('/nologo', '/noconfig', '/nostdlib+', "/target:$target", '/langversion:latest', '/nullable:enable',
        '/optimize+', '/debug-', '/deterministic+', '/platform:anycpu', '/filealign:512',
        "/win32manifest:$(Join-Path $root 'app.manifest')", "/out:$out") + $extra + $refs + $sources
    & dotnet $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc falló (código $LASTEXITCODE)" }
}

# 0) Pruebas: si alguna falla, no se genera nada.
$tests = Join-Path $build 'MajinDesktop.Tests.exe'
Invoke-Csc $tests ($appSources + (Join-Path $root 'tests\Tests.cs')) @('/main:MajinDesktop.Tests') 'exe'
& $tests
if ($LASTEXITCODE -ne 0) { throw 'Fallan las pruebas' }

# 1) Compilación previa para generar el icono con el mismo código que lo dibuja en la bandeja.
$stage = Join-Path $build 'MajinDesktop.stage.exe'
$ico = Join-Path $build 'MajinDesktop.ico'
Invoke-Csc $stage $appSources @()
Start-Process -FilePath $stage -ArgumentList '--make-icon', "`"$ico`"" -Wait -WindowStyle Hidden
if (-not (Test-Path $ico)) { throw 'No se generó el icono' }

# 2) App final con icono.
$exe = Join-Path $dist 'MajinDesktop.exe'
Invoke-Csc $exe $appSources @("/win32icon:$ico")

# 3) Instalador con la app incrustada.
$setup = Join-Path $dist "MajinDesktop-Setup-$Version.exe"
Invoke-Csc $setup $setupSources @("/win32icon:$ico", "/resource:$exe,MajinDesktop.exe")

foreach ($f in $exe, $setup) { '{0}  ({1:N0} KB)' -f $f, ((Get-Item $f).Length / 1KB) }
