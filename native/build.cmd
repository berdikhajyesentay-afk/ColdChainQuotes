@echo off
rem Builds the native tier library for win-x64 with MinGW-w64 gcc 15.2.0 (x86_64-w64-mingw32).
gcc -std=c11 -Wall -Wextra -O2 -shared -static-libgcc -o "%~dp0ccq_native.dll" "%~dp0ccq_native.c"
