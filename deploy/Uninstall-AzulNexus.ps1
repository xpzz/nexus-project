<#
.SYNOPSIS
    Remove o Azul Nexus do servidor: site e pool no IIS, serviço Worker, regra de firewall e binários.

.DESCRIPTION
    Preserva dados, configuração e backups, salvo -RemoveData. Nada fora do servidor é removido sozinho:
    o script gera, em <dados>\scripts, a reversão da concessão no SQL do SCCM e o roteiro de limpeza do Entra ID.
    Códigos de saída: 0 sucesso | 10 bloqueio | 20 falha.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$RemoveData,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$exitCode = 0
try {
    if (-not (Test-Administrator)) {
        Stop-Install -ExitCode 10 -WhatHappened 'O script não está em execução como administrador.' -Impact 'Nada foi removido.' -HowToFix 'Abra um PowerShell com "Executar como administrador".'
    }
    $key = Get-ItemProperty 'HKLM:\SOFTWARE\Azul\Nexus' -ErrorAction SilentlyContinue
    if (-not $key -or -not $key.InstallDir) {
        Stop-Install -ExitCode 10 -WhatHappened 'O Azul Nexus não está registrado neste servidor (HKLM\SOFTWARE\Azul\Nexus).' -Impact 'Nada foi removido.' -HowToFix 'Se restaram itens soltos, remova manualmente o site, o pool e o serviço AzulNexus.Worker.'
    }
    $installDir = $key.InstallDir
    $dataDir = $key.DataDir
    $siteName = if ($key.SiteName) { $key.SiteName } else { 'Azul Nexus' }
    $poolName = if ($key.AppPoolName) { $key.AppPoolName } else { 'AzulNexus' }
    Initialize-Log -Path (Join-Path $dataDir ("logs\uninstall-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss')))
    Write-Log "Remoção do Azul Nexus (versão $($key.Version))." 'STEP'

    if ($RemoveData -and -not $Force) {
        $answer = Read-Host "Isto apagará DEFINITIVAMENTE '$dataDir' (configuração, logs, chaves e backups). Digite APAGAR para confirmar"
        if ($answer -cne 'APAGAR') {
            Stop-Install -ExitCode 10 -WhatHappened 'A remoção dos dados não foi confirmada.' -Impact 'Nada foi removido.' -HowToFix 'Rode de novo e digite APAGAR, ou omita -RemoveData para preservar os dados.'
        }
    }

    # Scripts de reversão fora do servidor: gerados antes de apagar os binários (nexusctl lê a configuração).
    $nexusctl = Join-Path $installDir 'app\nexusctl.exe'
    $scripts = Join-Path $dataDir 'scripts'
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    if (Test-Path $nexusctl) {
        $env:NEXUS_DATA_DIR = $dataDir
        $workerAccount = if (Get-Service -Name 'AzulNexus.Worker' -ErrorAction SilentlyContinue) { 'NT SERVICE\AzulNexus.Worker' } else { $null }
        $service = Get-CimInstance Win32_Service -Filter "Name='AzulNexus.Worker'" -ErrorAction SilentlyContinue
        if ($service -and $service.StartName -and $service.StartName -notlike 'LocalSystem') { $workerAccount = $service.StartName }
        if ($workerAccount) {
            $revoke = Invoke-Native -FilePath $nexusctl -Arguments @('sccm-grant-script', '--account', $workerAccount, '--revoke', '--output', (Join-Path $scripts 'sccm-revoke.sql')) -AllowFailure
            if ($revoke.ExitCode -eq 0) { Write-Log "Script de reversão do SQL do SCCM: $(Join-Path $scripts 'sccm-revoke.sql') (entregue ao DBA; não foi executado)." }
        }
    }
    Set-Content -Path (Join-Path $scripts 'entra-limpeza.txt') -Encoding UTF8 -Value @(
        'Azul Nexus: itens fora do servidor que precisam de remoção manual (nada é removido automaticamente):',
        '- Registros de aplicativo "Azul Nexus - Web", "Azul Nexus - Coletor" e "Azul Nexus - Relatórios" no Microsoft Entra ID (se existirem).',
        '- Login e usuário da conta do serviço no SQL do SCCM e no banco do Nexus (use sccm-revoke.sql).',
        '- Banco do Nexus no servidor SQL/PostgreSQL, se não for mais necessário.'
    )

    if (Get-Service -Name 'AzulNexus.Worker' -ErrorAction SilentlyContinue) {
        Write-Log 'Removendo o serviço AzulNexus.Worker...'
        Stop-Service -Name 'AzulNexus.Worker' -Force -ErrorAction SilentlyContinue
        $service = Get-Service -Name 'AzulNexus.Worker' -ErrorAction SilentlyContinue
        if ($service) { try { $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) } catch { } }
        & sc.exe delete 'AzulNexus.Worker' | Out-Null
    }

    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\InetStp') {
        Import-Module WebAdministration -ErrorAction SilentlyContinue
        if (Get-Module WebAdministration) {
            if (Test-Path "IIS:\Sites\$siteName") {
                Write-Log "Removendo o site '$siteName'..."
                $port = (Get-WebBinding -Name $siteName -Protocol https | Select-Object -First 1).bindingInformation -split ':' | Select-Object -Skip 1 -First 1
                Stop-Website -Name $siteName -ErrorAction SilentlyContinue
                Remove-Website -Name $siteName
                if ($port -and (Test-Path "IIS:\SslBindings\0.0.0.0!$port")) { Remove-Item "IIS:\SslBindings\0.0.0.0!$port" -Force }
            }
            if (Test-Path "IIS:\AppPools\$poolName") {
                Write-Log "Removendo o pool '$poolName'..."
                Remove-WebAppPool -Name $poolName
            }
        }
    }

    Get-NetFirewallRule -DisplayName 'Azul Nexus HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $appDir = Join-Path $installDir 'app'
    $cleaned = (($machinePath -split ';') | Where-Object { $_ -and $_ -ne $appDir }) -join ';'
    if ($cleaned -ne $machinePath) { [Environment]::SetEnvironmentVariable('Path', $cleaned, 'Machine') }

    foreach ($sub in 'web', 'app', 'deploy') {
        $path = Join-Path $installDir $sub
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    if ((Test-Path $installDir) -and -not (Get-ChildItem $installDir -Force)) { Remove-Item $installDir -Force }

    if ($RemoveData) {
        Write-Log "Removendo os dados em $dataDir..." 'WARN'
        Remove-Item $dataDir -Recurse -Force
        Remove-Item 'HKLM:\SOFTWARE\Azul\Nexus' -Recurse -Force
    } else {
        # Mantém DataDir no registro para que uma reinstalação reencontre os dados.
        Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Azul\Nexus' -Name 'InstallDir', 'Version', 'SiteName', 'AppPoolName', 'Url' -ErrorAction SilentlyContinue
        Write-Log "Dados preservados em $dataDir (configuração, logs, chaves, backups e scripts)." 
    }
    Write-Log 'Remoção concluída. Itens fora do servidor: veja scripts\entra-limpeza.txt e scripts\sccm-revoke.sql.' 'STEP'
} catch {
    $exitCode = if ($_.Exception.Data.Contains('ExitCode')) { [int]$_.Exception.Data['ExitCode'] } else { 20 }
    $message = if ($_.Exception.Data.Contains('ExitCode')) { $_.Exception.Message } else { "O que aconteceu: $($_.Exception.Message)`nImpacto: a remoção ficou incompleta.`nComo resolver: rode o script de novo; ele é idempotente." }
    Write-Log $message 'ERROR'
}
exit $exitCode
