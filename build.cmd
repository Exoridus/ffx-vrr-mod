@echo off
setlocal
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSLANG=1033"
pushd "%~dp0"
dotnet run --project "%~dp0build\distribution\ModBuild.csproj" -- "%~dp0." %*
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%
