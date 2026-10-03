@echo off
rem Azul Nexus - configura o Entra ID e o Intune (registros, certificados, consentimento, funcoes) e valida.
rem Rode NO SERVIDOR do Nexus, com a conta de Administrador Global a mao (login por codigo de dispositivo).
rem Argumentos opcionais sao repassados: -AdminUser ana@empresa.com -ValidateOnly -SkipOptionalPermissions ...
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Solicitando permissao de administrador...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
  exit /b
)
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse | Unblock-File"
powershell -NoProfile -ExecutionPolicy Bypass -File "deploy\Install-AzulNexusAzure.ps1" %*
echo.
echo Codigo de saida: %errorlevel%  ^(0 = sucesso, 10 = bloqueio, 20 = falha, 30 = criado mas a validacao ficou pendente^)
pause
