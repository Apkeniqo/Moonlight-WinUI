@echo off
rem Early FFmpeg decode probe (harness.cpp). Not part of the shipping path;
rem the real decode/present code is mlrender.cpp.
setlocal
call "%~dp0env.bat"
if errorlevel 1 exit /b 1
cd /d "%~dp0"

cl /nologo /MD /utf-8 /EHsc /std:c++17 /W3 /O2 /I"%LIBS%\include\x64" harness.cpp ^
   /Fe:harness.exe /link /LIBPATH:"%LIBS%\lib\x64" avcodec.lib avformat.lib avutil.lib ^
   /MACHINE:X64 /DEBUG:NONE /INCREMENTAL:NO /PDB:NONE
exit /b %ERRORLEVEL%
