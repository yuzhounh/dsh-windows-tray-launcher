@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo The Windows .NET Framework C# compiler was not found.
  echo.
  pause
  exit /b 1
)
set "LOG=%~dp0install.log"
set "REFS=/reference:System.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Windows.Forms.dll"

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:"%~dp0DeepSeekHarnessTray.bootstrap.exe" %REFS% "%~dp0DeepSeekHarnessTray.cs" > "%LOG%" 2>&1
if errorlevel 1 goto failed

"%~dp0DeepSeekHarnessTray.bootstrap.exe" --make-icon "%~dp0DeepSeekHarness.ico" "%~dp0dsh-favicon-black.svg" >> "%LOG%" 2>&1
if errorlevel 1 goto failed

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /win32icon:"%~dp0DeepSeekHarness.ico" /out:"%~dp0DeepSeekHarnessTray.exe" %REFS% "%~dp0DeepSeekHarnessTray.cs" >> "%LOG%" 2>&1
if errorlevel 1 goto failed

del /q "%~dp0DeepSeekHarnessTray.bootstrap.exe" 2>nul
del /q "%LOG%" 2>nul

rem Hand over to the installer window and let this console close immediately.
start "" "%~dp0DeepSeekHarnessTray.exe" --install
exit /b 0

:failed
echo Build failed. Details follow.
echo.
if exist "%LOG%" type "%LOG%"
echo.
pause
exit /b 1
