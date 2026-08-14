@echo off
setlocal
if not exist "%~dp0DeepSeekHarnessTray.exe" (
  echo DeepSeekHarnessTray.exe was not found next to this script. Run Install.cmd first.
  echo.
  pause
  exit /b 1
)

rem Hand over to the uninstaller window and let this console close immediately.
start "" "%~dp0DeepSeekHarnessTray.exe" --uninstall
exit /b 0
