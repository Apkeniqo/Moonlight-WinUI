@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "%~dp0"
cl /nologo /LD /MD /utf-8 /EHsc /std:c++17 /W3 /O2 ^
 /I"E:\deps\ffmpeg-src\FFmpeg-946fcce" ^
 /I"E:\deps\opus-include" ^
 /I"E:\deps\mlc-include" ^
 /I"%~dp0openssl\include" ^
 mlrender.cpp mlsession.cpp mlaudio.cpp hud.cpp streamwin.cpp panel.cpp perfcard.cpp "%~dp0openssl\include\openssl\applink.c" ^
 /Fe:mlrender.dll /link ^
 /LIBPATH:"E:\deps\exports" /LIBPATH:"%~dp0mlc-build" /LIBPATH:"%~dp0mlc-build\enet" /LIBPATH:"%~dp0openssl\lib" ^
 avcodec.lib avformat.lib avutil.lib swscale.lib moonlight-common-c.lib enet.lib libssl.lib libcrypto.lib opus.lib ^
 ole32.lib ws2_32.lib winmm.lib crypt32.lib bcrypt.lib ^
 /MACHINE:X64 /DEBUG:NONE /INCREMENTAL:NO /PDB:NONE
