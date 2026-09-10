@echo off
chcp 65001 >nul
echo ==========================================
echo  MailLoadTester GUI — Self-Contained Build
echo ==========================================
echo.

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET 8 SDK not found.
    echo Download: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/3] Restoring packages...
dotnet restore
if errorlevel 1 pause && exit /b 1

echo [2/3] Building self-contained EXE for win-x64...
dotnet publish src\MailLoadTester.Gui -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 pause && exit /b 1

echo [3/3] Done.
echo.
echo Output: %CD%\publish\MailLoadTester.exe
echo.
pause
