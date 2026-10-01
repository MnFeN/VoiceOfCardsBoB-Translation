@echo off
setlocal
cd /d "%~dp0"
set EXE=SourceCode\VoiceOfCardsBoB-Translation\bin\Release\net10.0\VoiceOfCardsBoB-Translation.exe
if exist "%EXE%" (
  "%EXE%"
  exit /b %errorlevel%
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The program has not been built and .NET 10 SDK was not found.
  pause
  exit /b 1
)

dotnet run --project "SourceCode\VoiceOfCardsBoB-Translation\VoiceOfCardsBoB-Translation.csproj" -c Release
