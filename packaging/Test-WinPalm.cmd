@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0WinPalm.exe" goto extract
if not exist "%~dp0winpalm_core.dll" goto extract
if not exist "%~dp0web\index.html" goto extract
set "REPORT=%TEMP%\WinPalm-selftest.json"
set "INPUTREPORT=%TEMP%\WinPalm-input-test.json"
if exist "%REPORT%" del /q "%REPORT%"
if exist "%INPUTREPORT%" del /q "%INPUTREPORT%"
echo Checking WinPalm's native library, private storage, web server, pairing and revocation...
start "" /wait "%~dp0WinPalm.exe" --self-test --report "%REPORT%"
set "TESTCODE=%ERRORLEVEL%"
if exist "%REPORT%" type "%REPORT%"
if not "%TESTCODE%"=="0" goto failed
echo.
echo The optional input test opens a disposable editor and types and clicks only there.
echo Leave your mouse and keyboard idle for about 10 seconds while it runs.
choice /c YN /n /m "Run the native input test? [Y/N] "
if errorlevel 2 goto done
start "" /wait "%~dp0WinPalm.exe" --input-test --report "%INPUTREPORT%"
set "INPUTCODE=%ERRORLEVEL%"
if exist "%INPUTREPORT%" type "%INPUTREPORT%"
if not "%INPUTCODE%"=="0" echo The native input check failed. Please share its report.
echo.
echo Input report: %INPUTREPORT%
goto done
:extract
echo Extract the whole WinPalm ZIP to a folder first ^(right-click it, Extract All^), then run Test-WinPalm.cmd from that folder.
pause
exit /b 1
:failed
echo.
echo A check failed. Send the report below back to the developer.
:done
echo.
echo Server report: %REPORT%
echo Audio, HTTPS phone microphone, brightness, gestures and sleep still need the manual checks in README.md.
pause
endlocal
