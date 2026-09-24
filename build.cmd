@echo off
rem Compila ClassicForever.exe con el compilador de C# que ya trae Windows (.NET Framework 4.8).
rem No hace falta instalar Visual Studio ni el SDK de .NET. Lo mismo corre en GitHub Actions.
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" ( echo No encuentro %FW%\csc.exe & exit /b 1 )
if not exist dist mkdir dist
"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ^
  /out:dist\ClassicForever.exe /win32icon:src\app.ico /win32manifest:src\app.manifest ^
  /resource:src\MainWindow.xaml,ForeverLauncher.MainWindow.xaml ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Web.Extensions.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
echo OK: dist\ClassicForever.exe
