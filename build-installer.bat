@echo off
chcp 65001 >nul
echo ==========================================
echo  MailLoadTester GUI — Build + Installer
echo ==========================================
echo.

if not exist "publish\MailLoadTester.exe" (
    echo [INFO] EXE not found. Building first...
    call build.bat
    if errorlevel 1 exit /b 1
)

where iscc >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Inno Setup Compiler not found in PATH.
    echo Download: https://jrsoftware.org/isdl.php
    pause
    exit /b 1
)

echo [1/2] Compiling installer...
iscc installer\MailLoadTester.iss
if errorlevel 1 pause && exit /b 1

echo [2/2] Done.
echo.
echo Installer: %CD%\installer\Output\Setup-MailLoadTester.exe
echo.
pause
