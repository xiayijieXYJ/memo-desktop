@echo off
setlocal
cd /d "%~dp0"

echo [1/2] Publishing single-file executable...
REM Note: EnableCompressionInSingleFile only works with self-contained; omitted here.
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 goto failed

echo [2/2] Done. Output: %~dp0publish\Memo.exe
dir /b publish\Memo.exe
exit /b 0

:failed
echo Publish FAILED.
exit /b 1
