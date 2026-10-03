@echo off
rem Azul Nexus - verifica o ambiente sem alterar nada (Install-AzulNexus.ps1 -DetectOnly).
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Solicitando permissao de administrador...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse | Unblock-File"
if not exist "deploy\install.json" (
  copy "deploy\install.sample.json" "deploy\install.json" >nul
  echo O arquivo de respostas foi criado. Informe database.server e iis.hostName, salve e feche o Bloco de Notas.
  notepad "deploy\install.json"
)
powershell -NoProfile -ExecutionPolicy Bypass -File "deploy\Install-AzulNexus.ps1" -DetectOnly
echo.
echo Nada foi alterado. Codigo de saida: %errorlevel%
pause
