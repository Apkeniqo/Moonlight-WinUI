@echo off
rem Builds the WinUI 3 app with Visual Studio's MSBuild.
rem
rem Why not `dotnet build`: WinUI's PRI generation task lives in the VS MSBuild
rem extension folder; the plain .NET SDK fails with MSB4062
rem (Microsoft.Build.Packaging.Pri.Tasks.dll not found). MSBuild is located via
rem vswhere, so this script carries no machine specific path.
rem
rem Usage: build.bat [Debug^|Release]      (default: Debug, always x64)
setlocal
set "CFG=%~1"
if "%CFG%"=="" set "CFG=Debug"

set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath`) do (
    if exist "%%i\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%%i\MSBuild\Current\Bin\MSBuild.exe"
)

for %%p in (
  "D:\Program Files\Microsoft Visual Studio\18\Community"
  "D:\Microsoft Visual Studio\18\Community"
  "%ProgramFiles%\Microsoft Visual Studio\2022\Community"
  "%ProgramFiles%\Microsoft Visual Studio\2022\Professional"
  "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise"
  "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools"
) do if not defined MSBUILD if exist "%%~p\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%%~p\MSBuild\Current\Bin\MSBuild.exe"

if not defined MSBUILD (
    echo [build] ERROR: MSBuild.exe not found. Install Visual Studio with the .NET desktop workload.
    exit /b 1
)

echo [build] MSBuild: %MSBUILD%
"%MSBUILD%" -restore "%~dp0MoonlightWinUI.csproj" /p:Configuration=%CFG% /p:Platform=x64 /v:m /nologo
if errorlevel 1 exit /b 1
echo [build] OK: bin\x64\%CFG%\net8.0-windows10.0.19041.0\win-x64\MoonlightWinUI.exe
exit /b 0
