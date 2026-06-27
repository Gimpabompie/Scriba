@echo off
REM Bouwt lokaal op Windows een losse notulen.exe met PyInstaller.
REM Vereist dat Python 3.10+ is geinstalleerd (met "Add to PATH" aangevinkt).

echo === Afhankelijkheden installeren ===
python -m pip install --upgrade pip || goto :error
pip install -r requirements.txt pyinstaller || goto :error

echo.
echo === EXE bouwen (dit kan enkele minuten duren) ===
pyinstaller --noconfirm --onefile --name notulen ^
  --collect-all faster_whisper ^
  --collect-all ctranslate2 ^
  --collect-all av ^
  --collect-all tokenizers ^
  --collect-all onnxruntime ^
  --collect-all sounddevice ^
  app_entry.py || goto :error

echo.
echo === Klaar! ===
echo De app staat hier:  dist\notulen.exe
echo Dubbelklik dat bestand om te starten.
pause
exit /b 0

:error
echo.
echo Er ging iets mis. Controleer of Python is geinstalleerd en staat in PATH.
pause
exit /b 1
