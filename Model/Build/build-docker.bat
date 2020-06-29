@echo off
setlocal enableDelayedExpansion

if "%APSIM%"=="" (
	pushd %~dp0..\..
	set "APSIM=!cd!"
	popd
)

docker build -t buildapsim %~dp0Docker.build.latest
if errorlevel 1 exit /b 1

docker run -m 6g --cpu-count %NUMBER_OF_PROCESSORS% -v "%APSIM%":C:\APSIM buildapsim

endlocal