@echo off
rem Azul Nexus - instalacao com duplo clique. Pede permissao de administrador, desbloqueia os arquivos,
rem cria o install.json (se faltar) e roda Install-AzulNexus.ps1. Argumentos extras sao repassados ao script.
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Solicitando permissao de administrador...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
  exit /b
)

powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse | Unblock-File"

if not exist "deploy\install.json" (
  copy "deploy\install.sample.json" "deploy\install.json" >nul
  echo.
  echo O arquivo de respostas foi criado. Informe pelo menos o servidor do banco ^(database.server^)
  echo e o nome de acesso ^(iis.hostName^). Salve e feche o Bloco de Notas para continuar.
  notepad "deploy\install.json"
)

set "EXTRA=%*"
if exist "prereq\dotnet-hosting*.exe" if not exist "%ProgramFiles%\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll" (
  echo.
  echo O ASP.NET Core Hosting Bundle ainda nao esta instalado. O instalador incluido no pacote
  echo reinicia o IIS: em servidor de site do SCCM isso interrompe por instantes o management point
  echo e o distribution point. Faca isso em janela de manutencao.
  choice /c SN /m "Instalar o Hosting Bundle agora e reiniciar o IIS"
  if not errorlevel 2 set "EXTRA=%EXTRA% -AllowIisRestart"
)

powershell -NoProfile -ExecutionPolicy Bypass -File "deploy\Install-AzulNexus.ps1" %EXTRA%
echo.
echo Codigo de saida: %errorlevel%  ^(0 = sucesso, 10 = bloqueio de pre-requisito, 20 = falha com desfazer, 30 = instalado mas o site nao respondeu: nada foi desfeito, veja o diagnostico no log^)
pause
