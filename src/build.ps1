# Builds UxPlayMirror.exe and UxPlayMirrorSetup.exe into the parent folder,
# using the C# compiler that ships with Windows (.NET Framework 4.8)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$out = Split-Path $src
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fw\csc.exe"

if (-not (Test-Path "$src\app.ico")) { & "$src\MakeIcon.ps1" | Out-Null }
Copy-Item "$src\app.ico" "$out\app.ico" -Force

$refs = 'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xaml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'Microsoft.CSharp.dll', 'Microsoft.VisualBasic.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll'
$cargs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$out\UxPlayMirror.exe",
          "/win32icon:$src\app.ico", "/win32manifest:$src\app.manifest")
$cargs += $refs | ForEach-Object { "/reference:$fw\$_" }
$cargs += 'Theme.xaml', 'MainWindow.xaml', 'Toolbar.xaml', 'PenBar.xaml', 'NowPlaying.xaml' | ForEach-Object { "/resource:$src\$_,UxPlayMirror.$_" }
$cargs += Get-ChildItem "$src\*.cs" | ForEach-Object { $_.FullName }

& $csc @cargs
if ($LASTEXITCODE -ne 0) { throw "build failed" }
"built $out\UxPlayMirror.exe"

# Setup.exe: installer/uninstaller with the app embedded (copy it to another PC and run it)
$sargs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$out\UxPlayMirrorSetup.exe",
           "/win32icon:$src\app.ico", "/win32manifest:$src\app.manifest")
$sargs += $refs | ForEach-Object { "/reference:$fw\$_" }
$sargs += "/resource:$src\Theme.xaml,Setup.Theme.xaml", "/resource:$src\setup\Setup.xaml,Setup.Setup.xaml",
          "/resource:$src\app.ico,Setup.app.ico", "/resource:$out\UxPlayMirror.exe,Setup.UxPlayMirror.exe"
$sargs += "$src\setup\Setup.cs", "$src\Core.cs", "$src\StreamInfo.cs"
& $csc @sargs
if ($LASTEXITCODE -ne 0) { throw "setup build failed" }
"built $out\UxPlayMirrorSetup.exe"
