@echo off
rem Publish TextInOCR as a native Windows ARM64 binary, no Visual Studio required.
rem Uses the WinArm64 profile in TextInOCR\Properties\PublishProfiles\.
rem
rem Usage:
rem   publish_arm64.bat               win-arm64 single-file -> C:\PIG\clitools
rem
rem Both profiles publish TextInOCR.exe into the same folder, so run this one on
rem (or for) the ARM64 machine only; it replaces any x64 build already there.
rem Requirement on the target machine: the ARM64 .NET 10 runtime.

setlocal

set "PROFILE=WinArm64"
set "PROJ=%~dp0TextInOCR\TextInOCR.csproj"

echo Publishing "%PROJ%" with profile %PROFILE% ...
dotnet publish "%PROJ%" /p:PublishProfile=%PROFILE%
if errorlevel 1 (
    echo.
    echo Publish FAILED.
    exit /b %errorlevel%
)

echo.
echo Publish succeeded.
endlocal
