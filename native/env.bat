@echo off
rem ===========================================================================
rem Shared environment discovery for the native build scripts:
rem   build-mlc.bat / build-native.bat / build-msprobe.bat / build-harness.bat
rem
rem Dependencies are now BUNDLED with this repo (native\third_party), so the
rem external Moonlight-Qt checkout is no longer required:
rem   LIBS     native\third_party\libs\windows        FFmpeg + opus headers/libs
rem   MLC      native\third_party\moonlight-common-c  MLC sources (+ enet, nanors)
rem   OPENSSL  native\openssl                         shipped with the repo
rem   VCVARS   Visual Studio vcvars64.bat (auto-detected)
rem
rem Resolution order:
rem   1. native\third_party                        (preferred, self-contained)
rem   2. %ML_DEPS% + moonlight-common-c            (old Qt checkout, compat only)
rem   3. ..\..\Moonlight-Qt-with-FluentUI / D:\Projects / E:\Projects (compat)
rem ===========================================================================

set "LIBS="
set "MLC="
set "OPENSSL=%~dp0openssl"
set "VCVARS="
set "ML_DEPS_OK="

rem --- 1) bundled third_party ---
if exist "%~dp0third_party\libs\windows\include\x64\libavcodec\avcodec.h" (
    set "ML_DEPS_OK=%~dp0third_party"
    set "LIBS=%~dp0third_party\libs\windows"
    set "MLC=%~dp0third_party\moonlight-common-c"
)

rem --- 2) external ML_DEPS (Qt layout: MLC lives in a same-named nested dir) ---
if not defined MLC if defined ML_DEPS if exist "%ML_DEPS%\libs\windows\include\x64\libavcodec\avcodec.h" (
    set "ML_DEPS_OK=%ML_DEPS%"
    set "LIBS=%ML_DEPS%\libs\windows"
    set "MLC=%ML_DEPS%\moonlight-common-c\moonlight-common-c"
)

rem --- 3) fallback probing ---
if not defined MLC if exist "%~dp0..\..\Moonlight-Qt-with-FluentUI\libs\windows\include\x64\libavcodec\avcodec.h" (
    for %%i in ("%~dp0..\..\Moonlight-Qt-with-FluentUI") do set "ML_DEPS_OK=%%~fi"
    set "LIBS=%~dp0..\..\Moonlight-Qt-with-FluentUI\libs\windows"
    set "MLC=%~dp0..\..\Moonlight-Qt-with-FluentUI\moonlight-common-c\moonlight-common-c"
)
if not defined MLC if exist "D:\Projects\Moonlight-Qt-with-FluentUI\libs\windows\include\x64\libavcodec\avcodec.h" (
    set "ML_DEPS_OK=D:\Projects\Moonlight-Qt-with-FluentUI"
    set "LIBS=D:\Projects\Moonlight-Qt-with-FluentUI\libs\windows"
    set "MLC=D:\Projects\Moonlight-Qt-with-FluentUI\moonlight-common-c\moonlight-common-c"
)
if not defined MLC if exist "E:\Projects\Moonlight-Qt-with-FluentUI\libs\windows\include\x64\libavcodec\avcodec.h" (
    set "ML_DEPS_OK=E:\Projects\Moonlight-Qt-with-FluentUI"
    set "LIBS=E:\Projects\Moonlight-Qt-with-FluentUI\libs\windows"
    set "MLC=E:\Projects\Moonlight-Qt-with-FluentUI\moonlight-common-c\moonlight-common-c"
)

if not defined MLC (
    echo [env] ERROR: native\third_party is incomplete - FFmpeg headers or moonlight-common-c missing.
    echo [env] Expected: %~dp0third_party\libs\windows  and  %~dp0third_party\moonlight-common-c
    exit /b 1
)

if not exist "%MLC%\src\Limelight.h" (
    echo [env] ERROR: moonlight-common-c headers not found under "%MLC%\src"
    exit /b 1
)

rem --- Visual Studio -------------------------------------------------------
call "%~dp0vs-env.bat"
if errorlevel 1 exit /b 1

echo [env] deps   = %ML_DEPS_OK%
echo [env] libs   = %LIBS%
echo [env] mlc    = %MLC%
echo [env] vcvars = %VCVARS%
exit /b 0
