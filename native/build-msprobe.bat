@echo off
rem Console probe: links against mlrender.lib, no UI. Run after build-native.bat.
setlocal
call "%~dp0env.bat"
if errorlevel 1 exit /b 1
cd /d "%~dp0"

cl /nologo /MD /utf-8 /EHsc /std:c++17 /W3 /O2 msprobe.cpp ^
   /Fe:msprobe.exe /link /LIBPATH:"%~dp0" mlrender.lib ^
   /MACHINE:X64 /DEBUG:NONE /INCREMENTAL:NO /PDB:NONE
exit /b %ERRORLEVEL%
