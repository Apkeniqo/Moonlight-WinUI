@echo off
rem Rebuilds moonlight-common-c.lib + enet.lib into native\mlc-build.
rem Sources come from native\third_party\moonlight-common-c (self-contained repo),
rem so no external checkout is required. Delete mlc-build first if the sources
rem moved, because CMakeCache.txt stores absolute paths.
setlocal
call "%~dp0env.bat"
if errorlevel 1 exit /b 1

rem --- locate cmake: PATH first, then the copy bundled with Visual Studio ---
set "CMAKE="
for %%i in (cmake.exe) do if not "%%~$PATH:i"=="" set "CMAKE=%%~$PATH:i"
if not defined CMAKE if defined VCVARS (
    for %%v in ("%VCVARS%\..\..\..\..\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe") do (
        if exist "%%~fv" set "CMAKE=%%~fv"
    )
)
if not defined CMAKE (
    for %%v in ("%ProgramFiles%\CMake\bin\cmake.exe" "%LOCALAPPDATA%\Programs\CMake\bin\cmake.exe") do (
        if exist "%%~v" set "CMAKE=%%~v"
    )
)
if not defined CMAKE (
    echo [mlc] ERROR: cmake not found ^(install CMake or use the one bundled with VS^)
    exit /b 1
)

set "BUILD=%~dp0mlc-build"
if not exist "%BUILD%" mkdir "%BUILD%"
cd /d "%BUILD%"

echo [mlc] cmake  = %CMAKE%
echo [mlc] source = %MLC%

"%CMAKE%" -G "NMake Makefiles" ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DBUILD_SHARED_LIBS=OFF ^
  -DCODE_ANALYSIS=OFF ^
  -DBUILD_TESTING=OFF ^
  -DCMAKE_C_FLAGS="/utf-8 /wd4819 /WX-" ^
  -DOPENSSL_ROOT_DIR="%OPENSSL%" ^
  -DOPENSSL_INCLUDE_DIR="%OPENSSL%\include" ^
  -DOPENSSL_CRYPTO_LIBRARY="%OPENSSL%\lib\libcrypto.lib" ^
  -DOPENSSL_SSL_LIBRARY="%OPENSSL%\lib\libssl.lib" ^
  "%MLC%" || exit /b 1

nmake || exit /b 1
exit /b 0
