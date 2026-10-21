@echo off
setlocal
cd /d "%~dp0"

REM Self-contained single-file build: bundles the .NET 8 runtime inside,
REM so the target machine needs nothing preinstalled. Much bigger (~64MB).
echo [1/3] Publishing self-contained single-file executable...
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None -p:DebugSymbols=false ^
  -o dist\Memo-portable
if errorlevel 1 goto failed

echo [2/3] Copying usage guide...
if exist "docs\使用说明.txt" copy /y "docs\使用说明.txt" "dist\Memo-portable\使用说明.txt" >nul 2>&1

echo [3/3] Done. Output: %~dp0dist\Memo-portable\Memo.exe
for %%F in ("dist\Memo-portable\Memo.exe") do echo Size: %%~zF bytes
exit /b 0

:failed
echo Publish FAILED.
exit /b 1