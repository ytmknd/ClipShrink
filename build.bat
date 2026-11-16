@echo off
chcp 65001 >nul
rem ビルドして app フォルダに出力します（引数 portable でランタイム同梱の単一 EXE）
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET SDK が見つかりません。https://dotnet.microsoft.com/download から入手してください。
  pause
  exit /b 1
)

if /i "%~1"=="portable" (
  dotnet publish ClipShrink.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o portable
  echo 完了: portable\ClipShrink.exe
) else (
  dotnet publish ClipShrink.csproj -c Release -r win-x64 --self-contained false -o app
  echo 完了: app\ClipShrink.exe
)
pause
