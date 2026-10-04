<#
.SYNOPSIS
    Configura o Azul Nexus no Microsoft Entra ID e no Intune: cria os registros de aplicativo, os certificados, o
    consentimento do administrador, as funções de acesso e grava a configuração no servidor.

.DESCRIPTION
    Rode NO SERVIDOR do Nexus, em um PowerShell como administrador, com a conta de Administrador Global (ou Administrador
    de Aplicativos + Administrador de Função Privilegiada) à mão para o login por código de dispositivo.

    O que faz (idempotente: pode rodar de novo; só cria ou concede o que faltar):
      1. Gera dois certificados de cliente em LocalMachine\My (chave NÃO exportável) e dá à conta do serviço a leitura da chave.
      2. Exporta os .cer públicos para <dados>\scripts\azure.
      3. Login do administrador (device code). O token fica só em memória.
      4. Cria "Azul Nexus – Coletor" (permissões de aplicativo somente leitura do Intune/Entra) e concede o consentimento.
      5. Cria "Azul Nexus – Web" (SSO): URIs de redirecionamento, funções Nexus.Leitura/Analista/AdminIntegracao/Auditoria, atribuição
         obrigatória, consentimento das permissões delegadas e atribui o administrador à função Nexus.AdminIntegracao.
      6. Grava <dados>\config\azure.json (somente identificadores e impressões digitais, sem segredos).
      7. Valida com a identidade do aplicativo (token por certificado, permissões e uma chamada por permissão).

    Códigos de saída: 0 sucesso | 10 bloqueio (nada foi criado) | 20 falha | 30 criado, mas a validação ficou pendente.

.PARAMETER PublicUrl
    Endereço público do Nexus (padrão: o registrado na instalação, ex.: https://nexus.empresa.local:8443).

.PARAMETER AdminUser
    UPN do primeiro administrador do Nexus (padrão: a conta que fizer o login).

.PARAMETER TenantId
    ID (ou domínio) do locatário. Padrão: o da conta que fizer o login.

.PARAMETER WorkerAccount
    Conta do serviço AzulNexus.Worker (lê a chave do certificado do Coletor). Padrão: a conta configurada no serviço.

.PARAMETER WebAccount
    Conta do serviço AzulNexus.Web (lê a chave do certificado da Web). Padrão: a conta configurada no serviço.

.PARAMETER SkipOptionalPermissions
    Não pede Organization.Read.All (licenças contratadas).

.PARAMETER ValidateOnly
    Só valida (usa o azure.json já gravado); não cria nem altera nada no Entra ID.
#>
[CmdletBinding()]
param(
    [string]$PublicUrl,
    [string]$AdminUser,
    [string]$TenantId,
    [string]$WorkerAccount,
    [string]$WebAccount,
    [int]$CertificateYears = 2,
    [switch]$SkipOptionalPermissions,
    [switch]$ValidateOnly,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Azure.ps1')
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Conta configurada em um serviço do Windows (ou, se não existir, o valor padrão informado).
function Get-ServiceAccountName {
    param([string]$ServiceName, [string]$Fallback)
    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($service -and $service.StartName -and $service.StartName -notmatch '^LocalSystem$') { return $service.StartName }
    return $Fallback
}

# Reaproveita um certificado válido (mais de 30 dias) com o mesmo assunto; senão cria um novo, com chave não exportável.
function Confirm-NexusCertificate {
    param([string]$Subject, [string]$FriendlyName, [int]$Years)
    $existing = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($existing) {
        Write-Log "Certificado '$Subject' reaproveitado (vence em $($existing.NotAfter.ToString('yyyy-MM-dd')))."
        return $existing
    }
    Write-Log "Gerando o certificado '$Subject' (RSA 2048, chave não exportável, $Years ano(s))."
    return New-SelfSignedCertificate -Subject $Subject -FriendlyName $FriendlyName -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -KeySpec Signature -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears($Years) -CertStoreLocation Cert:\LocalMachine\My
}

function Write-ValidationReport {
    param($Report)
    foreach ($line in $Report) {
        $level = switch ($line.Status) { 'OK' { 'INFO' } 'Atenção' { 'WARN' } default { 'ERROR' } }
        Write-Log ("[{0}] {1}: {2}" -f $line.Status, $line.Item, $line.Detail) $level
    }
}

$exitCode = 0
try {
    if (-not (Test-Administrator)) {
        Stop-Install -ExitCode 10 -WhatHappened 'O script não está em execução como administrador.' -Impact 'Nada foi criado.' -HowToFix 'Abra um PowerShell com "Executar como administrador" no servidor do Nexus e rode de novo.'
    }
    $key = Get-ItemProperty 'HKLM:\SOFTWARE\Azul\Nexus' -ErrorAction SilentlyContinue
    if (-not $key -or -not $key.DataDir) {
        Stop-Install -ExitCode 10 -WhatHappened 'O Azul Nexus não está instalado neste servidor (HKLM\SOFTWARE\Azul\Nexus ausente).' -Impact 'Nada foi criado.' -HowToFix 'Rode este script no servidor onde o Instalar.cmd concluiu a instalação.'
    }
    $dataDir = $key.DataDir
    if (-not $LogPath) { $LogPath = Join-Path $dataDir ("logs\azure-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    Initialize-Log -Path $LogPath
    $configFile = Join-Path $dataDir 'config\azure.json'
    $outDir = Join-Path $dataDir 'scripts\azure'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    if (-not $PublicUrl) { $PublicUrl = $key.Url }
    if (-not $PublicUrl) { Stop-Install -ExitCode 10 -WhatHappened 'Não foi possível descobrir o endereço público do Nexus.' -Impact 'Nada foi criado.' -HowToFix 'Informe -PublicUrl https://<nome>:<porta>.' }
    Write-Log "Azul Nexus no Azure: início ($PublicUrl)." 'STEP'

    $workerAccount = if ($WorkerAccount) { $WorkerAccount } else { Get-ServiceAccountName -ServiceName 'AzulNexus.Worker' -Fallback 'NT SERVICE\AzulNexus.Worker' }
    $webAccount = if ($WebAccount) { $WebAccount } else { Get-ServiceAccountName -ServiceName 'AzulNexus.Web' -Fallback $workerAccount }

    if ($ValidateOnly) {
        if (-not (Test-Path $configFile)) { Stop-Install -ExitCode 10 -WhatHappened "Não existe $configFile." -Impact 'Não há o que validar.' -HowToFix 'Rode o script sem -ValidateOnly primeiro.' }
        $config = Get-Content $configFile -Raw | ConvertFrom-Json
        $collectorCert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $config.collector.certificateThumbprint } | Select-Object -First 1
        if (-not $collectorCert) { Stop-Install -ExitCode 10 -WhatHappened "O certificado $($config.collector.certificateThumbprint) não está em LocalMachine\My." -Impact 'Não dá para validar.' -HowToFix 'Rode o script sem -ValidateOnly para recriar o certificado e o registro.' }
        $report = Test-NexusAzureAccess -TenantId $config.tenantId -ClientId $config.collector.clientId -Certificate $collectorCert -IncludeOptional:(-not $SkipOptionalPermissions)
        Write-ValidationReport $report
        if (@($report | Where-Object Status -eq 'Falha').Count -gt 0) { $exitCode = 30 }
        exit $exitCode
    }

    # 1) Certificados
    $collectorCert = Confirm-NexusCertificate -Subject 'CN=AzulNexus-Coletor' -FriendlyName 'Azul Nexus – Coletor (Entra ID)' -Years $CertificateYears
    $webCert = Confirm-NexusCertificate -Subject 'CN=AzulNexus-Web' -FriendlyName 'Azul Nexus – Web (SSO)' -Years $CertificateYears
    Grant-CertificateKeyAccess -Account $workerAccount -Thumbprint $collectorCert.Thumbprint
    Grant-CertificateKeyAccess -Account $webAccount -Thumbprint $webCert.Thumbprint
    foreach ($pair in @(@{ Cert = $collectorCert; File = 'nexus-coletor.cer' }, @{ Cert = $webCert; File = 'nexus-web.cer' })) {
        $target = Join-Path $outDir $pair.File
        [IO.File]::WriteAllBytes($target, $pair.Cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Write-Log "Certificado público: $target (impressão digital $($pair.Cert.Thumbprint))."
    }

    # 2) Entra ID
    $token = Get-AdminToken -TenantId $(if ($TenantId) { $TenantId } else { 'organizations' })
    $tenantFromToken = (Get-JwtPayload $token).tid
    $result = Initialize-NexusAzure -Token $token -PublicUrl $PublicUrl -CollectorCertificate $collectorCert -WebCertificate $webCert -AdminUpn $AdminUser -SkipOptionalPermissions:$SkipOptionalPermissions
    if ($tenantFromToken -and $tenantFromToken -ne $result.TenantId) { Write-Log "Atenção: o locatário do token ($tenantFromToken) difere do informado pelo diretório ($($result.TenantId))." 'WARN' }
    $token = $null

    # 3) Configuração no servidor (sem segredos)
    $config = New-AzureConfig -Result $result -CollectorCertSubject $collectorCert.Subject -WebCertSubject $webCert.Subject
    $configDir = Split-Path -Parent $configFile
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    ($config | ConvertTo-Json -Depth 6) | Set-Content -Path $configFile -Encoding UTF8
    Write-Log "Configuração gravada em $configFile."

    # 4) Validação com a identidade do aplicativo (o consentimento pode levar alguns minutos para propagar)
    Write-Log 'Validando com a identidade do aplicativo (pode levar alguns minutos para o consentimento propagar)...' 'STEP'
    $report = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $report = Test-NexusAzureAccess -TenantId $result.TenantId -ClientId $result.Collector.AppId -Certificate $collectorCert -IncludeOptional:(-not $SkipOptionalPermissions)
        if (@($report | Where-Object Status -eq 'Falha').Count -eq 0) { break }
        if ($attempt -lt 6) { Write-Log "Tentativa $attempt com pendências; nova tentativa em 30 segundos." 'WARN'; Start-Sleep -Seconds 30 }
    }
    Write-ValidationReport $report
    Write-Log 'Resumo do que foi feito:' 'STEP'
    foreach ($action in $result.Actions) { Write-Log "  - $action" }
    if (@($report | Where-Object Status -eq 'Falha').Count -gt 0) {
        $exitCode = 30
        Write-Log 'Os registros foram criados, mas a validação ainda tem falhas. Aguarde alguns minutos e rode: .\Install-AzulNexusAzure.ps1 -ValidateOnly' 'WARN'
    } else {
        Write-Log "Concluído. Locatário $($result.TenantName); Coletor $($result.Collector.AppId); Web $($result.Web.AppId); administrador $($result.Web.Admin)." 'STEP'
    }
    Write-Log 'Observação: esta versão do Nexus ainda não lê o Intune nem faz login por SSO; a configuração fica pronta para quando esses módulos forem ativados.' 'WARN'
} catch {
    $exitCode = if ($_.Exception.Data.Contains('ExitCode')) { [int]$_.Exception.Data['ExitCode'] } else { 20 }
    $message = if ($_.Exception.Data.Contains('ExitCode')) { $_.Exception.Message } else { "O que aconteceu: $(Get-GraphErrorDetail $_)`nImpacto: a configuração do Azure ficou incompleta (o que já foi criado é reaproveitado ao rodar de novo).`nComo resolver: confirme que a conta que fez o login é Administrador Global e rode o script de novo; ele é idempotente." }
    Write-Log $message 'ERROR'
}
exit $exitCode
