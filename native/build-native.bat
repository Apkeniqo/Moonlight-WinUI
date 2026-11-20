@echo off
rem Builds mlrender.dll from every native source in this folder.
rem Dependencies (FFmpeg/opus headers+libs, MLC sources, OpenSSL) all come from
rem native\third_party + native\openssl via env.bat - no external repo needed.
setlocal
call "%~dp0env.bat"
if errorlevel 1 exit /b 1
cd /d "%~dp0"

cl /nologo /LD /MD /utf-8 /EHsc /std:c++17 /W3 /O2 ^
   /I"%LIBS%\include\x64" ^
   /I"%LIBS%\include" ^
   /I"%MLC%\src" ^
   /I"%OPENSSL%\include" ^
   mlrender.cpp mlsession.cpp mlaudio.cpp hud.cpp streamwin.cpp panel.cpp perfcard.cpp ^
   "%OPENSSL%\include\openssl\applink.c" ^
   /Fe:mlrender.dll /link ^
   /LIBPATH:"%LIBS%\lib\x64" ^
   /LIBPATH:"%~dp0mlc-build" ^
   /LIBPATH:"%~dp0mlc-build\enet" ^
   /LIBPATH:"%OPENSSL%\lib" ^
   avcodec.lib avformat.lib avutil.lib swscale.lib ^
   moonlight-common-c.lib enet.lib libssl.lib libcrypto.lib ^
   opus.lib ole32.lib ^
   ws2_32.lib winmm.lib crypt32.lib bcrypt.lib ^
   /MACHINE:X64 /DEBUG:NONE /INCREMENTAL:NO /PDB:NONE
exit /b %ERRORLEVEL%
