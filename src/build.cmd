@echo off
rem ============================================================
rem  Build SkillManager.exe
rem
rem  Uses the C# compiler that ships with Windows (.NET Framework
rem  4.0), so nothing needs to be installed: no Visual Studio,
rem  no .NET SDK, no network access.
rem
rem  Keep this file ASCII-only and keep the compiler invocation on
rem  ONE line. cmd.exe mishandles both Chinese text (encoding) and
rem  caret line-continuation here.
rem ============================================================
setlocal
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "FW=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\WPF"
set "HERE=%~dp0"
set "OUT=%HERE%..\SkillManager.exe"

rem 32-bit Windows has no Framework64: fall back to the 32-bit compiler.
if not exist "%CSC%" (
  set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
  set "FW=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
)

if not exist "%CSC%" (
  echo [ERROR] C# compiler not found: %CSC%
  echo         .NET Framework 4.x is probably missing on this machine.
  exit /b 1
)

echo Building...
rem Optional application icon (src\app.ico). Skip it silently when absent.
set "ICON="
if exist "%HERE%app.ico" set ICON=/win32icon:"%HERE%app.ico"
rem /codepage:65001 makes the UTF-8 source readable on non-Chinese
rem Windows too (without it some compilers assume the ANSI codepage).
"%CSC%" /nologo /target:winexe /codepage:65001 /out:"%OUT%" %ICON% /reference:"%FW%\System.dll" /reference:"%FW%\System.Core.dll" /reference:"%FW%\System.Xaml.dll" /reference:"%WPF%\PresentationCore.dll" /reference:"%WPF%\PresentationFramework.dll" /reference:"%WPF%\WindowsBase.dll" /reference:"%FW%\System.Windows.Forms.dll" /reference:"%FW%\System.Drawing.dll" "%HERE%SkillManager.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  exit /b 1
)

echo.
echo Done: %OUT%
echo.
echo To verify, run this from the portable folder:
echo   SkillManager.exe --selftest --out report.txt
endlocal
