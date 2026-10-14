@echo off
setlocal

set "TASK_ROOT=%~dp0.."
set "BUILD_PYTHON=%TASK_ROOT%\.venv-hdr\Scripts\python.exe"
if not exist "%BUILD_PYTHON%" (
    echo [ERROR] The approved project build environment is missing.
    echo Expected: %BUILD_PYTHON%
    echo This script does not install software or modify global Python.
    pause
    exit /b 1
)

"%BUILD_PYTHON%" -c "import PyInstaller, imagecodecs, numpy" >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Approved packaging or runtime dependencies are missing.
    pause
    exit /b 1
)

echo.
echo Building EXE...
"%BUILD_PYTHON%" -m PyInstaller ^
  --console ^
  --onefile ^
  --name GainMapWorker ^
  --hidden-import imagecodecs._shared ^
  --hidden-import imagecodecs._shared_cython ^
  --hidden-import imagecodecs._jpegxr ^
  --hidden-import imagecodecs._ultrahdr ^
  --copy-metadata imagecodecs ^
  --copy-metadata numpy ^
  --distpath "%TASK_ROOT%\.work-ui\worker-dist" ^
  --workpath "%TASK_ROOT%\.work-ui\worker-build" ^
  --specpath "%TASK_ROOT%\.work-ui\worker-spec" ^
  "%~dp0gainmap_worker.py"
if errorlevel 1 (
    echo [ERROR] Build failed.
    pause
    exit /b 1
)

set "CS_COMPILER=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "CS_FRAMEWORK=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%CS_COMPILER%" (
    echo [ERROR] The existing Windows WPF compiler is unavailable.
    pause
    exit /b 1
)
if not exist "%TASK_ROOT%\release" mkdir "%TASK_ROOT%\release"
if not exist "%TASK_ROOT%\release" (
    echo [ERROR] Cannot create the release directory.
    pause
    exit /b 1
)
"%CS_COMPILER%" /nologo /target:winexe /platform:x64 ^
  "/out:%TASK_ROOT%\release\GainMap.exe" ^
  "/win32icon:%~dp0assets\gainmap-app.ico" ^
  "/win32manifest:%~dp0GainMap.manifest" ^
  "/resource:%~dp0GlassUi.xaml,GainMap.UI" ^
  "/resource:%~dp0assets\gainmap-app.png,GainMap.Icon" ^
  "/resource:%TASK_ROOT%\.work-ui\worker-dist\GainMapWorker.exe,GainMap.Worker" ^
  "/reference:%CS_FRAMEWORK%\WPF\PresentationFramework.dll" ^
  "/reference:%CS_FRAMEWORK%\WPF\PresentationCore.dll" ^
  "/reference:%CS_FRAMEWORK%\WPF\WindowsBase.dll" ^
  "/reference:%CS_FRAMEWORK%\System.Xaml.dll" ^
  "/reference:%CS_FRAMEWORK%\System.Web.Extensions.dll" ^
  "/reference:%CS_FRAMEWORK%\System.Windows.Forms.dll" ^
  "%~dp0GlassApp.cs" "%~dp0GlassControls.cs" "%~dp0WorkerClient.cs" "%~dp0NativeFolderPicker.cs"
if errorlevel 1 (
    echo [ERROR] Native UI build failed.
    pause
    exit /b 1
)

echo.
echo Build finished.
echo Standalone EXE:
echo %TASK_ROOT%\release\GainMap.exe
pause
