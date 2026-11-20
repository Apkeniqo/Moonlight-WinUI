@echo off
rem Finds Visual Studio's vcvars64.bat (vswhere first, known paths as fallback)
rem and calls it. Shared by env.bat and the standalone native builds.
rem Exports VCVARS; no other machine specific paths.
set "VCVARS="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
    if exist "%%i\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
)

for %%p in (
  "D:\Program Files\Microsoft Visual Studio\18\Community"
  "D:\Microsoft Visual Studio\18\Community"
  "%ProgramFiles%\Microsoft Visual Studio\2022\Community"
  "%ProgramFiles%\Microsoft Visual Studio\2022\Professional"
  "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise"
  "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools"
  "%ProgramFiles%\Microsoft Visual Studio\2019\Community"
  "%ProgramFiles%\Microsoft Visual Studio\2019\BuildTools"
) do if not defined VCVARS if exist "%%~p\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%%~p\VC\Auxiliary\Build\vcvars64.bat"

if not defined VCVARS (
    echo [vs-env] ERROR: vcvars64.bat not found. Install the VS C++ build tools.
    exit /b 1
)

call "%VCVARS%" >nul 2>&1
if errorlevel 1 (
    echo [vs-env] ERROR: failed to initialise "%VCVARS%"
    exit /b 1
)
exit /b 0
