@echo off
setlocal
pushd "%~dp0"
dotnet run --project "%~dp0build\distribution\ModBuild.csproj" -- "%~dp0." %*
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%
