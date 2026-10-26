@echo off
rem Publish TextInOCR as a win-x64 binary, no Visual Studio required.
rem Uses the FolderProfile in TextInOCR\Properties\PublishProfiles\.
rem
rem Usage:
rem   publish_x64.bat                 win-x64 single-file -> C:\PIG\clitools
rem
rem For a native Windows ARM64 build, use publish_arm64.bat instead.
rem Requirement on the target machine: the x64 .NET 10 runtime.

setlocal

set "PROFILE=FolderProfile"
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
