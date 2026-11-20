@echo off
rem Builds pairtls.dll - HTTPS GET with a client certificate, used by the
rem pairing "pairchallenge" stage. Needs only OpenSSL (bundled in this repo),
rem not the Moonlight-Qt checkout, so it builds on either machine.
setlocal
call "%~dp0vs-env.bat"
if errorlevel 1 exit /b 1
cd /d "%~dp0"

set "OPENSSL=%~dp0openssl"

cl /nologo /LD /MD /utf-8 /EHsc /std:c++17 /W3 /O2 ^
   /I"%OPENSSL%\include" ^
   pairtls.cpp "%OPENSSL%\include\openssl\applink.c" ^
   /Fe:pairtls.dll /link ^
   /LIBPATH:"%OPENSSL%\lib" ^
   libssl.lib libcrypto.lib ws2_32.lib crypt32.lib ^
   /MACHINE:X64 /DEBUG:NONE /INCREMENTAL:NO /PDB:NONE
exit /b %ERRORLEVEL%
