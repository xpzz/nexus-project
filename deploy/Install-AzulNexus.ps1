<#
.SYNOPSIS
    Instala, atualiza ou repara o Azul Nexus: site no IIS, serviço Worker, banco, permissões e primeira coleta.

.DESCRIPTION
    Idempotente: rodar de novo repara; rodar com um pacote de outra versão atualiza (backup antes da migração).
    Execute em um PowerShell elevado, na pasta do pacote gerado por Publish-AzulNexus.ps1.

    Códigos de saída: 0 sucesso | 3010 reinício necessário | 10 bloqueio de pré-requisito | 20 falha com desfazer concluído.

.PARAMETER Answers
    Arquivo de respostas (padrão: install.json ao lado deste script; veja install.sample.json).

.PARAMETER DetectOnly
    Só detecta e valida o ambiente; não altera nada.

.PARAMETER AllowIisRestart
    Autoriza a instalação do Hosting Bundle (-HostingBundleInstaller), que reinicia o IIS. Em servidor de site do SCCM,
    isso derruba temporariamente o management point e o distribution point.

.PARAMETER AllowSelfSigned
    Se não houver certificado adequado, gera um autoassinado (somente piloto).

.PARAMETER ServiceAccountPassword
    Senha da conta única dos serviços (serviceIdentity.account, ex.: svc.sccm). Alternativa: variável NEXUS_SERVICE_PASSWORD;
    sem nenhuma das duas, a senha é pedida no console. Nunca vai para log nem para arquivo; o Windows a guarda
    protegida (LSA para o serviço, applicationHost.config criptografado para o pool).

.PARAMETER DatabasePassword
    Senha do usuário do banco (somente provedor PostgreSql). Alternativa: variável de ambiente NEXUS_DB_PASSWORD.
#>
[CmdletBinding()]
param(
    [string]$Answers,
    [switch]$DetectOnly,
    [switch]$AllowIisRestart,
    [string]$HostingBundleInstaller,
    [switch]$AllowSelfSigned,
    [switch]$SkipBackup,
    [switch]$NoOpenBrowser,
    [string]$LogPath,
    [securestring]$DatabasePassword,
    [securestring]$ServiceAccountPassword
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

$script:PackageRoot = Split-Path -Parent $PSScriptRoot
$script:Undo = New-Object System.Collections.Stack
$script:Pending = New-Object System.Collections.Generic.List[string]
$script:Nexusctl = $null
$script:RebootRequired = $false
$script:ServicePassword = $null

#region Utilitários do servidor

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PackageVersion {
    $file = Join-Path $script:PackageRoot 'VERSION.txt'
    if (Test-Path $file) { return (Get-Content $file -Raw).Trim() }
    return '0.0.0'
}

function Invoke-Nexusctl {
    param([string[]]$Arguments, [switch]$AllowFailure, [switch]$Quiet)
    $result = Invoke-Native -FilePath $script:Nexusctl -Arguments $Arguments -AllowFailure:$AllowFailure
    if (-not $Quiet) { foreach ($line in $result.Output) { Write-Log "  nexusctl: $line" } }
    return $result
}

function Grant-Acl {
    param([string]$Path, [string[]]$Grants, [switch]$ResetInheritance)
    $arguments = @($Path)
    if ($ResetInheritance) { $arguments += '/inheritance:r' }
    foreach ($grant in $Grants) { $arguments += @('/grant:r', $grant) }
    $arguments += '/Q'
    Invoke-Native -FilePath 'icacls.exe' -Arguments $arguments | Out-Null
}

function Wait-ServiceStatus {
    param([string]$Name, [string]$Status, [int]$TimeoutSeconds = 60)
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) { return }
    try { $service.WaitForStatus($Status, [TimeSpan]::FromSeconds($TimeoutSeconds)) } catch { }
}

function Test-TcpEndpoint {
    param([string]$HostName, [int]$Port, [int]$TimeoutMs = 4000)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
        $client.EndConnect($async)
        return $true
    } catch { return $false } finally { $client.Close() }
}

#endregion

#region Detecção e contexto

function Get-SccmDetection {
    $result = [ordered]@{ SiteCode = $null; SqlServer = $null; Database = $null; Source = 'não detectado' }
    try {
        $identification = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\SMS\Identification' -ErrorAction Stop
        $result.SiteCode = $identification.'Site Code'
        if ($result.SiteCode) { $result.Source = 'registro do site' }
    } catch { }
    try {
        $location = Get-CimInstance -Namespace 'root\SMS' -ClassName SMS_ProviderLocation -ErrorAction Stop | Where-Object ProviderForLocalSite | Select-Object -First 1
        if ($location) {
            if (-not $result.SiteCode) { $result.SiteCode = $location.SiteCode }
            $namespace = "root\SMS\site_$($result.SiteCode)"
            $sql = Get-CimInstance -Namespace $namespace -ClassName SMS_SCI_SysResUse -Filter "RoleName='SMS SQL Server' AND SiteCode='$($result.SiteCode)'" -ErrorAction Stop | Select-Object -First 1
            if ($sql) {
                $name = $null
                foreach ($property in @($sql.Props)) {
                    if ($property.PropertyName -eq 'SQL Server Name') {
                        $name = @($property.Value2, $property.Value1, $property.Value) | Where-Object { $_ } | Select-Object -First 1
                    }
                    if ($property.PropertyName -eq 'Database Name') {
                        $result.Database = @($property.Value2, $property.Value1, $property.Value) | Where-Object { $_ } | Select-Object -First 1
                    }
                }
                if (-not $name -and $sql.NetworkOSPath) { $name = $sql.NetworkOSPath.TrimStart('\') }
                $result.SqlServer = $name
                $result.Source = 'SMS Provider (WMI)'
            }
        }
    } catch { Write-Log "SMS Provider indisponível para detecção: $($_.Exception.Message)" 'WARN' }
    $result['DatabaseGuessed'] = $false
    if ($result.SiteCode -and -not $result.Database) { $result.Database = "CM_$($result.SiteCode)"; $result['DatabaseGuessed'] = $true }
    return [pscustomobject]$result
}

function Get-DomainDetection {
    try {
        $system = Get-CimInstance Win32_ComputerSystem
        if ($system.PartOfDomain) { return $system.Domain }
    } catch { }
    return $null
}

function Resolve-Context {
    param($Raw)
    $ctx = [ordered]@{}
    $ctx.Version = Get-PackageVersion

    $installDir = Get-Setting $Raw 'installDir' 'auto'
    $ctx.InstallDir = if (Test-IsAuto $installDir) { Join-Path $env:ProgramFiles 'Azul Nexus' } else { $installDir }

    $dataDir = Get-Setting $Raw 'dataDir' 'auto'
    if (Test-IsAuto $dataDir) {
        $disks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID, FreeSpace)
        $dataDir = Select-DataDirectory -Disks $disks
    }
    $ctx.DataDir = $dataDir

    $ctx.Iis = [pscustomobject]@{
        SiteName     = Get-Setting $Raw 'iis.siteName' 'Azul Nexus'
        AppPoolName  = Get-Setting $Raw 'iis.appPoolName' 'AzulNexus'
        Port         = [int](Get-Setting $Raw 'iis.port' 8443)
        HostName     = $null
        Certificate  = Get-Setting $Raw 'iis.certificate' 'auto'
        OpenFirewall = [bool](Get-Setting $Raw 'iis.openFirewall' $true)
    }
    $hostName = Get-Setting $Raw 'iis.hostName' 'auto'
    $ctx.Iis.HostName = if (Test-IsAuto $hostName) { (Get-DefaultHostName).ToLowerInvariant() } else { $hostName }
    $ctx.PublicUrl = 'https://{0}:{1}' -f $ctx.Iis.HostName, $ctx.Iis.Port

    $detected = Get-SccmDetection
    $ctx.SccmDetected = $detected
    $siteCode = Get-Setting $Raw 'sccm.site' 'auto'
    $sqlServer = Get-Setting $Raw 'sccm.sqlServer' 'auto'
    $sccmDatabase = Get-Setting $Raw 'sccm.database' 'auto'
    $ctx.SccmDatabaseGuessed = (Test-IsAuto $sccmDatabase) -and [bool]$detected.DatabaseGuessed
    $ctx.Sccm = [pscustomobject]@{
        SiteCode               = if (Test-IsAuto $siteCode) { $detected.SiteCode } else { $siteCode }
        SqlServer              = if (Test-IsAuto $sqlServer) { $detected.SqlServer } else { $sqlServer }
        Database               = if (Test-IsAuto $sccmDatabase) { $detected.Database } else { $sccmDatabase }
        GrantViewAccess        = Get-Setting $Raw 'sccm.grantViewAccess' 'if-permitted'
        TrustServerCertificate = [bool](Get-Setting $Raw 'sccm.trustServerCertificate' $false)
    }

    $domain = Get-Setting $Raw 'activeDirectory.domain' 'auto'
    $ctx.ActiveDirectory = [pscustomobject]@{
        Domain      = if (Test-IsAuto $domain) { Get-DomainDetection } else { $domain }
        Server      = Get-Setting $Raw 'activeDirectory.server' $null
        SearchBases = @(Get-Setting $Raw 'activeDirectory.searchBases' @())
        UseLdaps    = [bool](Get-Setting $Raw 'activeDirectory.useLdaps' $false)
    }

    $provider = Get-Setting $Raw 'database.provider' 'SqlServer'
    if ($provider -notin @('SqlServer', 'PostgreSql')) { $provider = 'SqlServer' }
    $dbRaw = Get-Setting $Raw 'database' ([pscustomobject]@{})
    $ctx.Database = [pscustomobject]@{
        Provider          = $provider
        Server            = Get-Setting $Raw 'database.server' ''
        Name              = Get-Setting $Raw 'database.name' 'AzulNexus'
        AllowSccmInstance = [bool](Get-Setting $Raw 'database.allowSccmInstance' $false)
        ConnectionString  = New-NexusConnectionString -Provider $provider -Database $dbRaw -TrustServerCertificate ([bool](Get-Setting $Raw 'database.trustServerCertificate' $false))
    }

    $account = Get-Setting $Raw 'serviceIdentity.account' $null
    $ctx.Accounts = Resolve-ServiceAccounts `
        -Account (Resolve-AccountName $account) `
        -WebGmsa (Get-Setting $Raw 'serviceIdentity.webGmsa' $null) `
        -WorkerGmsa (Get-Setting $Raw 'serviceIdentity.workerGmsa' $null) `
        -PoolName $ctx.Iis.AppPoolName -Provider $provider -NexusDbServer $ctx.Database.Server `
        -SccmSqlServer $ctx.Sccm.SqlServer -SccmLive ([bool]$ctx.Sccm.SqlServer)

    $ctx.Collection = Get-Setting $Raw 'collection' $null
    $ctx.DemoMode = [bool](Get-Setting $Raw 'demoMode' $false)
    return [pscustomobject]$ctx
}

#endregion

#region Verificações do ambiente (SPEC §3.3)

function Get-CurrentInstall {
    try {
        $key = Get-ItemProperty 'HKLM:\SOFTWARE\Azul\Nexus' -ErrorAction Stop
        if ($key.InstallDir) { return $key }
    } catch { }
    return $null
}

function Test-Environment {
    param($Ctx)
    $checks = New-Object System.Collections.Generic.List[object]

    $checks.Add((New-Check 'Administrador' $(if (Test-Administrator) { 'OK' } else { 'Bloqueio' }) 'Execução como administrador.' 'Abra um PowerShell com "Executar como administrador".'))

    $os = Get-CimInstance Win32_OperatingSystem
    $isServerOk = [Environment]::Is64BitOperatingSystem -and ([version]$os.Version -ge [version]'6.3')
    $checks.Add((New-Check 'Sistema operacional' $(if ($isServerOk) { 'OK' } else { 'Bloqueio' }) "$($os.Caption) ($($os.Version))." 'Use Windows Server 2012 R2 ou superior, 64 bits (recomendado: 2019+).'))

    $computer = Get-CimInstance Win32_ComputerSystem
    $checks.Add((New-Check 'Domínio' $(if ($computer.PartOfDomain) { 'OK' } else { 'Atenção' }) $(if ($computer.PartOfDomain) { "Membro do domínio $($computer.Domain)." } else { 'Servidor fora de domínio: a leitura do AD e as contas gMSA não funcionarão.' }) 'Ingresse o servidor no domínio.'))

    $drive = Split-Path -Qualifier $Ctx.DataDir
    $disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$drive'" -ErrorAction SilentlyContinue
    if ($disk) {
        $freeGb = [math]::Round($disk.FreeSpace / 1GB, 1)
        $checks.Add((New-Check 'Disco' $(if ($freeGb -ge 5) { 'OK' } else { 'Bloqueio' }) "$freeGb GB livres em $drive (pasta de dados)." 'Libere ao menos 5 GB ou escolha outro dataDir.'))
    }

    $iisInstalled = Test-Path 'HKLM:\SOFTWARE\Microsoft\InetStp'
    $checks.Add((New-Check 'IIS' $(if ($iisInstalled) { 'OK' } else { 'Bloqueio' }) $(if ($iisInstalled) { 'IIS instalado.' } else { 'IIS não instalado.' }) 'Install-WindowsFeature Web-Server, Web-WebSockets, Web-Scripting-Tools -IncludeManagementTools'))
    $webSockets = Test-Path (Join-Path $env:windir 'System32\inetsrv\iiswsock.dll')
    $checks.Add((New-Check 'IIS: WebSockets' $(if ($webSockets) { 'OK' } else { 'Bloqueio' }) $(if ($webSockets) { 'Recurso WebSockets presente (exigido pelo Blazor).' } else { 'Recurso WebSockets ausente.' }) 'Install-WindowsFeature Web-WebSockets'))
    $webAdmin = [bool](Get-Module -ListAvailable -Name WebAdministration)
    $checks.Add((New-Check 'IIS: ferramentas de script' $(if ($webAdmin) { 'OK' } else { 'Bloqueio' }) $(if ($webAdmin) { 'Módulo WebAdministration disponível.' } else { 'Módulo WebAdministration ausente.' }) 'Install-WindowsFeature Web-Scripting-Tools'))

    $moduleDll = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    $runtimeDir = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.AspNetCore.App'
    $runtime = if (Test-Path $runtimeDir) { Get-ChildItem $runtimeDir -Directory | Where-Object { $_.Name -like '10.*' } | Select-Object -First 1 } else { $null }
    $bundleOk = (Test-Path $moduleDll) -and $runtime
    if ($bundleOk) {
        $checks.Add((New-Check 'ASP.NET Core Hosting Bundle' 'OK' "Módulo do IIS e runtime $($runtime.Name) presentes."))
    } elseif ($HostingBundleInstaller -and $AllowIisRestart) {
        $checks.Add((New-Check 'ASP.NET Core Hosting Bundle' 'Atenção' 'Ausente; será instalado de forma silenciosa e o IIS será reiniciado (autorizado por -AllowIisRestart).'))
    } elseif ($HostingBundleInstaller) {
        $checks.Add((New-Check 'ASP.NET Core Hosting Bundle' 'Bloqueio' 'Hosting Bundle ausente. Há um instalador no pacote, mas instalá-lo reinicia o IIS e, em servidor de site do SCCM, interrompe por instantes o management point e o distribution point.' 'Em janela de manutenção, rode com -AllowIisRestart (o instalador do pacote será usado), ou use o Instalar.cmd e responda S.'))
    } else {
        $checks.Add((New-Check 'ASP.NET Core Hosting Bundle' 'Bloqueio' 'ASP.NET Core 10 Hosting Bundle ausente. A instalação reinicia o IIS e, em servidor de site do SCCM, interrompe por instantes o management point e o distribution point.' 'Instale o Hosting Bundle 10.x em janela de manutenção, ou rode este script com -HostingBundleInstaller <caminho do dotnet-hosting-10.x-win.exe> -AllowIisRestart.'))
    }

    $port = $Ctx.Iis.Port
    if (Test-PortReserved $port) {
        $checks.Add((New-Check 'Porta HTTPS' 'Bloqueio' "A porta $port conflita com portas do SCCM, do WSUS ou do IIS padrão." 'Use outra porta em iis.port (padrão 8443).'))
    } else {
        $inUse = $false
        try { $inUse = [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) } catch { }
        $ownSite = $false
        if ($webAdmin -and $iisInstalled) {
            try {
                Import-Module WebAdministration -ErrorAction Stop
                $ownSite = [bool](Get-WebBinding -Name $Ctx.Iis.SiteName -ErrorAction SilentlyContinue | Where-Object { $_.bindingInformation -like "*:${port}:*" })
            } catch { }
        }
        if ($inUse -and -not $ownSite) {
            $checks.Add((New-Check 'Porta HTTPS' 'Bloqueio' "A porta $port já está em uso por outro processo." 'Escolha outra porta em iis.port ou libere a porta.'))
        } else {
            $checks.Add((New-Check 'Porta HTTPS' 'OK' "Porta $port livre ou já usada pelo próprio Nexus."))
        }
    }

    if ($Ctx.Sccm.SqlServer) {
        if ($Ctx.SccmDatabaseGuessed) {
            $checks.Add((New-Check 'SCCM' 'Atenção' "Site $($Ctx.Sccm.SiteCode), SQL $($Ctx.Sccm.SqlServer). O nome do banco ($($Ctx.Sccm.Database)) é uma SUPOSIÇÃO a partir do código do site: o SMS Provider não informou o nome real." 'Confirme o nome do banco do site no SSMS (ex.: CM_XXX ou outro nome) e informe em sccm.database no install.json.'))
        } else {
            $checks.Add((New-Check 'SCCM' 'OK' "Site $($Ctx.Sccm.SiteCode), SQL $($Ctx.Sccm.SqlServer), banco $($Ctx.Sccm.Database) (origem: $($Ctx.SccmDetected.Source))."))
        }
    } elseif ($Ctx.DemoMode) {
        $checks.Add((New-Check 'SCCM' 'OK' 'Modo demonstração: dados sintéticos.'))
    } else {
        $checks.Add((New-Check 'SCCM' 'Atenção' 'Site SCCM não detectado neste servidor. A coleta do SCCM ficará "não configurada".' 'Informe sccm.sqlServer, sccm.database e sccm.site no arquivo de respostas (instalação fora do servidor do SCCM).'))
    }

    if ($Ctx.ActiveDirectory.Domain) {
        $checks.Add((New-Check 'Active Directory' 'OK' "Domínio $($Ctx.ActiveDirectory.Domain)."))
    } else {
        $checks.Add((New-Check 'Active Directory' 'Atenção' 'Domínio não detectado; a coleta do AD ficará "não configurada".' 'Informe activeDirectory.domain.'))
    }

    foreach ($check in (Test-NexusDatabaseChoice -Provider $Ctx.Database.Provider -Server $Ctx.Database.Server -Name $Ctx.Database.Name -SccmServer $Ctx.Sccm.SqlServer -SccmDatabase $Ctx.Sccm.Database -AllowSccmInstance $Ctx.Database.AllowSccmInstance)) {
        $checks.Add($check)
    }

    foreach ($problem in $Ctx.Accounts.Problems) {
        $checks.Add((New-Check 'Identidade dos serviços' 'Bloqueio' $problem 'Crie e instale a gMSA (New-ADServiceAccount / Install-ADServiceAccount) e informe-a em serviceIdentity. Conta virtual aparece na rede como a conta de computador do servidor.'))
    }
    if ($Ctx.Accounts.Mode -eq 'SharedAccount') {
        $account = $Ctx.Accounts.Account
        $sid = $null
        try { $sid = (New-Object System.Security.Principal.NTAccount($account)).Translate([System.Security.Principal.SecurityIdentifier]) } catch { }
        if ($sid) {
            $checks.Add((New-Check 'Conta dos serviços' 'OK' "Site (pool do IIS) e Worker rodarão sob $account (conta única, sem contas virtuais). Senha pedida na instalação."))
            $checks.Add((New-Check 'Conta dos serviços: privilégio' 'Atenção' "A conta $account provavelmente tem privilégios altos no SCCM e no SQL. O site (interface web) passará a rodar com ela, o que contraria a separação da SPEC §5.2 (o site não deveria alcançar SCCM/AD). O Nexus só executa SELECT, mas a exposição da interface é maior." 'Aceito por decisão do responsável (ADR-0002). Se possível, use uma conta própria com SELECT apenas nas views do SCCM.'))
        } else {
            $checks.Add((New-Check 'Conta dos serviços' 'Bloqueio' "A conta '$account' não foi encontrada neste servidor/domínio." 'Informe serviceIdentity.account como DOMINIO\conta (ex.: CORP\svc.sccm) e confirme que o servidor está no domínio.'))
        }
    }
    foreach ($gmsa in @($Ctx.Accounts.WebGmsa, $Ctx.Accounts.WorkerGmsa) | Where-Object { $_ }) {
        if (-not $gmsa.EndsWith('$')) {
            $checks.Add((New-Check 'Identidade dos serviços' 'Bloqueio' "A conta '$gmsa' não termina com '$' e não parece uma gMSA." 'Informe a gMSA como DOMINIO\nome$.'))
        } elseif (Get-Command Test-ADServiceAccount -ErrorAction SilentlyContinue) {
            $ok = $false
            try { $ok = Test-ADServiceAccount -Identity (($gmsa -split '\\')[-1].TrimEnd('$')) } catch { }
            $checks.Add((New-Check "gMSA $gmsa" $(if ($ok) { 'OK' } else { 'Atenção' }) $(if ($ok) { 'gMSA utilizável neste servidor.' } else { 'Não foi possível validar a gMSA neste servidor.' }) 'Install-ADServiceAccount <nome> e confirme PrincipalsAllowedToRetrieveManagedPassword.'))
        }
    }

    foreach ($endpoint in 'login.microsoftonline.com', 'graph.microsoft.com') {
        $reachable = Test-TcpEndpoint -HostName $endpoint -Port 443
        $checks.Add((New-Check "Internet: $endpoint" $(if ($reachable) { 'OK' } else { 'Atenção' }) $(if ($reachable) { 'Porta 443 acessível.' } else { 'Sem conexão direta na porta 443 (proxy ou firewall).' }) 'Libere o endpoint (e o armazenamento de blobs dos relatórios do Intune) no proxy. Necessário só para a etapa Azure.'))
    }

    if (Get-Command Get-AppLockerPolicy -ErrorAction SilentlyContinue) {
        try {
            $rules = @((Get-AppLockerPolicy -Effective).RuleCollections).Count
            if ($rules -gt 0) { $checks.Add((New-Check 'AppLocker' 'Atenção' 'Há política AppLocker ativa; ela pode bloquear os binários do Nexus.' 'Inclua a pasta de instalação nas regras de permissão.')) }
        } catch { }
    }

    return $checks
}

function Show-Checks {
    param($Checks)
    foreach ($check in $Checks) {
        $level = switch ($check.Status) { 'Bloqueio' { 'ERROR' } 'Atenção' { 'WARN' } default { 'INFO' } }
        Write-Log ('[{0}] {1}: {2}' -f $check.Status, $check.Name, $check.Message) $level
        if ($check.Status -ne 'OK' -and $check.Fix) { Write-Log "    Como resolver: $($check.Fix)" $level }
    }
}

#endregion

#region Etapas de instalação

function Install-HostingBundle {
    if (-not ($HostingBundleInstaller -and $AllowIisRestart)) { return }
    $moduleDll = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    if (Test-Path $moduleDll) { return }
    if (-not (Test-Path $HostingBundleInstaller)) {
        Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked -WhatHappened "Instalador do Hosting Bundle não encontrado em '$HostingBundleInstaller'." -Impact 'O site não pode rodar no IIS.' -HowToFix 'Baixe dotnet-hosting-10.x-win.exe e informe o caminho correto.'
    }
    Write-Log 'Instalando o ASP.NET Core Hosting Bundle (o IIS será reiniciado)...' 'STEP'
    $process = Start-Process -FilePath $HostingBundleInstaller -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    if ($process.ExitCode -eq 3010) { $script:RebootRequired = $true }
    elseif ($process.ExitCode -ne 0) {
        Stop-Install -ExitCode $script:ExitCodes.FailedRolledBack -WhatHappened "O Hosting Bundle terminou com código $($process.ExitCode)." -Impact 'O site não pode rodar no IIS.' -HowToFix 'Rode o instalador manualmente e veja o log em %TEMP%.'
    }
    Invoke-Native -FilePath 'iisreset.exe' -Arguments @('/restart') | Out-Null
}

function Publish-Files {
    param($Ctx, $Previous)
    Write-Log 'Copiando binários...' 'STEP'
    $webTarget = Join-Path $Ctx.InstallDir 'web'
    $appTarget = Join-Path $Ctx.InstallDir 'app'
    New-Item -ItemType Directory -Path $Ctx.InstallDir -Force | Out-Null

    $rollbackRoot = $null
    if ($Previous -and (Test-Path $webTarget)) {
        $rollbackRoot = Join-Path $Ctx.DataDir ("backups\bin-{0}-{1}" -f $Previous.Version, (Get-Date -Format 'yyyyMMddHHmmss'))
        New-Item -ItemType Directory -Path $rollbackRoot -Force | Out-Null
        Invoke-Native -FilePath 'robocopy.exe' -Arguments @($webTarget, (Join-Path $rollbackRoot 'web'), '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -AllowFailure | Out-Null
        if (Test-Path $appTarget) {
            Invoke-Native -FilePath 'robocopy.exe' -Arguments @($appTarget, (Join-Path $rollbackRoot 'app'), '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -AllowFailure | Out-Null
        }
        $script:Undo.Push({
            Write-Log "Desfazendo: restaurando binários de $rollbackRoot" 'WARN'
            Invoke-Native -FilePath 'robocopy.exe' -Arguments @((Join-Path $rollbackRoot 'web'), $webTarget, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -AllowFailure | Out-Null
            Invoke-Native -FilePath 'robocopy.exe' -Arguments @((Join-Path $rollbackRoot 'app'), $appTarget, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -AllowFailure | Out-Null
            Start-Service -Name 'AzulNexus.Worker' -ErrorAction SilentlyContinue
        }.GetNewClosure())
    }

    # Libera os arquivos do site antes de substituí-los (app_offline.htm descarrega o app sem derrubar o IIS).
    if (Test-Path $webTarget) {
        Set-Content -Path (Join-Path $webTarget 'app_offline.htm') -Value '<html><body><h1>Azul Nexus em atualização</h1><p>Volte em alguns instantes.</p></body></html>' -Encoding UTF8
        Start-Sleep -Seconds 3
    }
    if (Get-Service -Name 'AzulNexus.Worker' -ErrorAction SilentlyContinue) {
        Stop-Service -Name 'AzulNexus.Worker' -Force -ErrorAction SilentlyContinue
        Wait-ServiceStatus -Name 'AzulNexus.Worker' -Status 'Stopped'
    }

    $robocopyWeb = Invoke-Native -FilePath 'robocopy.exe' -Arguments @((Join-Path $script:PackageRoot 'web'), $webTarget, '/MIR', '/XF', 'app_offline.htm', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -AllowFailure
    $robocopyApp = Invoke-Native -FilePath 'robocopy.exe' -Arguments @((Join-Path $script:PackageRoot 'app'), $appTarget, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -AllowFailure
    if ($robocopyWeb.ExitCode -ge 8 -or $robocopyApp.ExitCode -ge 8) {
        Stop-Install -ExitCode $script:ExitCodes.FailedRolledBack -WhatHappened 'A cópia dos binários falhou (robocopy).' -Impact 'Instalação incompleta.' -HowToFix ('Verifique espaço em disco, antivírus e arquivos em uso em "{0}".' -f $Ctx.InstallDir)
    }
    $deployTarget = Join-Path $Ctx.InstallDir 'deploy'
    Invoke-Native -FilePath 'robocopy.exe' -Arguments @((Join-Path $script:PackageRoot 'deploy'), $deployTarget, '/MIR', '/XF', 'install.json', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -AllowFailure | Out-Null
    $script:Nexusctl = Join-Path $appTarget 'nexusctl.exe'
}

function Initialize-DataFolders {
    param($Ctx)
    foreach ($name in 'config', 'logs', 'keys', 'backups', 'scripts') {
        New-Item -ItemType Directory -Path (Join-Path $Ctx.DataDir $name) -Force | Out-Null
    }
    if (-not [System.Diagnostics.EventLog]::SourceExists('Azul Nexus')) {
        [System.Diagnostics.EventLog]::CreateEventSource('Azul Nexus', 'Application')
    }
}

function Set-RegistryInfo {
    param($Ctx)
    $path = 'HKLM:\SOFTWARE\Azul\Nexus'
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    Set-ItemProperty -Path $path -Name 'InstallDir' -Value $Ctx.InstallDir
    Set-ItemProperty -Path $path -Name 'DataDir' -Value $Ctx.DataDir
    Set-ItemProperty -Path $path -Name 'Version' -Value $Ctx.Version
    Set-ItemProperty -Path $path -Name 'SiteName' -Value $Ctx.Iis.SiteName
    Set-ItemProperty -Path $path -Name 'AppPoolName' -Value $Ctx.Iis.AppPoolName
    Set-ItemProperty -Path $path -Name 'Url' -Value $Ctx.PublicUrl
}

function Set-WorkerService {
    param($Ctx)
    Write-Log 'Configurando o serviço AzulNexus.Worker...' 'STEP'
    $name = 'AzulNexus.Worker'
    $exe = Join-Path $Ctx.InstallDir 'app\AzulNexus.Worker.exe'
    $existed = [bool](Get-Service -Name $name -ErrorAction SilentlyContinue)
    if (-not $existed) {
        Invoke-Native -FilePath 'sc.exe' -Arguments @('create', $name, 'binPath=', "`"$exe`"", 'start=', 'delayed-auto', 'DisplayName=', 'Azul Nexus Worker') | Out-Null
        $script:Undo.Push({ Stop-Service -Name 'AzulNexus.Worker' -Force -ErrorAction SilentlyContinue; & sc.exe delete 'AzulNexus.Worker' | Out-Null })
    } else {
        Invoke-Native -FilePath 'sc.exe' -Arguments @('config', $name, 'binPath=', "`"$exe`"", 'start=', 'delayed-auto') | Out-Null
    }
    Invoke-Native -FilePath 'sc.exe' -Arguments @('description', $name, 'Azul Nexus: coletas, verificações e agendador. Somente leitura nas fontes.') | Out-Null
    Invoke-Native -FilePath 'sc.exe' -Arguments @('failure', $name, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/60000') | Out-Null
    if ($Ctx.Accounts.Mode -eq 'SharedAccount') {
        # Método Change do serviço (CIM): a senha não aparece em linha de comando.
        $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
        $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ StartName = $Ctx.Accounts.Account; StartPassword = (ConvertTo-PlainText $script:ServicePassword) }
        if ($result.ReturnValue -ne 0) {
            Stop-Install -ExitCode $script:ExitCodes.FailedRolledBack -WhatHappened "O Windows recusou configurar o serviço para a conta $($Ctx.Accounts.Account) (código $($result.ReturnValue))." -Impact 'O Worker não consegue iniciar.' -HowToFix 'Confira a senha e se a conta tem o direito "Fazer logon como serviço" (se vier de GPO, inclua a conta na GPO).'
        }
    } elseif ($Ctx.Accounts.WorkerGmsa) {
        Invoke-Native -FilePath 'sc.exe' -Arguments @('config', $name, 'obj=', $Ctx.Accounts.WorkerGmsa, 'password=', '""') | Out-Null
    } else {
        Invoke-Native -FilePath 'sc.exe' -Arguments @('config', $name, 'obj=', 'NT SERVICE\AzulNexus.Worker') | Out-Null
    }
}

# Conta única: direito de logon como serviço (local; GPO pode sobrescrever) e membro de IIS_IUSRS.
function Set-ServiceAccountRights {
    param($Ctx)
    if ($Ctx.Accounts.Mode -ne 'SharedAccount') { return }
    $account = $Ctx.Accounts.Account
    $sid = (New-Object System.Security.Principal.NTAccount($account)).Translate([System.Security.Principal.SecurityIdentifier]).Value

    # Serviço (Worker) e trabalho em lote (pool do IIS). Em domínio, a GPO costuma sobrescrever a concessão local:
    # por isso o logon é TESTADO depois (Test-AccountLogons) e a falha é explicada.
    $rights = @(
        @{ Name = 'SeServiceLogonRight'; Label = "Fazer logon como serviço" },
        @{ Name = 'SeBatchLogonRight'; Label = "Fazer logon como trabalho em lote" })
    $work = Join-Path $env:TEMP ("nexus-secedit-{0}" -f [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $inf = Join-Path $work 'current.inf'
        $db = Join-Path $work 'secedit.sdb'
        Invoke-Native -FilePath 'secedit.exe' -Arguments @('/export', '/cfg', $inf, '/areas', 'USER_RIGHTS', '/quiet') | Out-Null
        $text = Get-Content $inf -Raw -Encoding Unicode
        $lines = @()
        foreach ($right in $rights) {
            if ($text -notmatch "$($right.Name)[^\r\n]*\*$sid") {
                $line = [regex]::Match($text, "$($right.Name)\s*=\s*(.*)")
                $entries = if ($line.Success -and $line.Groups[1].Value.Trim()) { $line.Groups[1].Value.Trim() + ',' } else { '' }
                $lines += "$($right.Name) = $entries*$sid"
                Write-Log "Direito '$($right.Label)' concedido localmente a $account."
            } else {
                Write-Log "A conta $account já tem o direito '$($right.Label)'."
            }
        }
        if ($lines.Count -gt 0) {
            $newInf = "[Unicode]`r`nUnicode=yes`r`n[Version]`r`nsignature=`"`$CHICAGO`$`"`r`nRevision=1`r`n[Privilege Rights]`r`n" + ($lines -join "`r`n") + "`r`n"
            $newPath = Join-Path $work 'grant.inf'
            Set-Content -Path $newPath -Value $newInf -Encoding Unicode
            Invoke-Native -FilePath 'secedit.exe' -Arguments @('/configure', '/db', $db, '/cfg', $newPath, '/areas', 'USER_RIGHTS', '/quiet') | Out-Null
        }
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    $member = (Invoke-Native -FilePath 'net.exe' -Arguments @('localgroup', 'IIS_IUSRS') -AllowFailure).Output -join "`n"
    if ($member -notmatch [regex]::Escape(($account -split '\\')[-1])) {
        Invoke-Native -FilePath 'net.exe' -Arguments @('localgroup', 'IIS_IUSRS', $account, '/add') -AllowFailure | Out-Null
    }
}

# Tenta logon real da conta como serviço e como trabalho em lote (o pool do IIS exige este) e explica a falha.
function Test-AccountLogons {
    param($Ctx)
    if ($Ctx.Accounts.Mode -ne 'SharedAccount') { return }
    if (-not ('NexusLogon' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NexusLogon
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    public static int Try(string user, string domain, string password, int logonType)
    {
        IntPtr token;
        if (LogonUser(user, domain, password, logonType, 0, out token)) { CloseHandle(token); return 0; }
        return Marshal.GetLastWin32Error();
    }
}
"@
    }
    $account = $Ctx.Accounts.Account
    $domain, $user = if ($account.Contains('\')) { $account.Split('\', 2) } else { $null, $account }
    $plain = ConvertTo-PlainText $script:ServicePassword
    $problems = @()
    foreach ($test in @(@{ Type = 'Service'; Value = 5 }, @{ Type = 'Batch'; Value = 4 })) {
        $code = [NexusLogon]::Try($user, $domain, $plain, $test.Value)
        if ($code -eq 0) { Write-Log "Logon de teste de $account (tipo $($test.Type)): OK."; continue }
        $verdict = Resolve-LogonFailure -Code $code -LogonType $test.Type -Account $account
        if ($verdict.Definitive) { $problems += $verdict } else { Write-Log "$($verdict.Summary) $($verdict.HowToFix)" 'WARN' }
    }
    if ($problems.Count -gt 0) {
        Stop-Install -ExitCode $script:ExitCodes.FailedRolledBack `
            -WhatHappened (($problems | ForEach-Object { $_.Summary }) -join ' ') `
            -Impact 'O pool do IIS (ou o Worker) não consegue iniciar com essa conta; a instalação foi desfeita antes de subir os serviços.' `
            -HowToFix (($problems | ForEach-Object { $_.HowToFix } | Select-Object -Unique) -join ' ')
    }
}

function Set-IisSite {
    param($Ctx, [string]$Thumbprint)
    Write-Log 'Configurando o site no IIS...' 'STEP'
    Import-Module WebAdministration -ErrorAction Stop
    $pool = $Ctx.Iis.AppPoolName
    $site = $Ctx.Iis.SiteName
    $port = $Ctx.Iis.Port
    $webDir = Join-Path $Ctx.InstallDir 'web'

    if (-not (Test-Path "IIS:\AppPools\$pool")) {
        New-WebAppPool -Name $pool | Out-Null
        $script:Undo.Push({ Remove-WebAppPool -Name $pool -ErrorAction SilentlyContinue }.GetNewClosure())
    }
    Set-ItemProperty "IIS:\AppPools\$pool" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode -Value 'AlwaysRunning'
    Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Set-ItemProperty "IIS:\AppPools\$pool" -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
    if ($Ctx.Accounts.Mode -eq 'SharedAccount') {
        Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel -Value @{ identityType = 'SpecificUser'; userName = $Ctx.Accounts.Account; password = (ConvertTo-PlainText $script:ServicePassword) }
    } elseif ($Ctx.Accounts.WebGmsa) {
        Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel -Value @{ identityType = 'SpecificUser'; userName = $Ctx.Accounts.WebGmsa; password = '' }
    } else {
        Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.identityType -Value 'ApplicationPoolIdentity'
    }

    if (-not (Test-Path "IIS:\Sites\$site")) {
        New-Website -Name $site -PhysicalPath $webDir -ApplicationPool $pool -Port $port -HostHeader $Ctx.Iis.HostName -Ssl -Force | Out-Null
        $script:Undo.Push({ Remove-Website -Name $site -ErrorAction SilentlyContinue }.GetNewClosure())
    } else {
        Set-ItemProperty "IIS:\Sites\$site" -Name physicalPath -Value $webDir
        Set-ItemProperty "IIS:\Sites\$site" -Name applicationPool -Value $pool
    }

    # Somente o binding HTTPS do Nexus; nunca mexe em sites do SCCM.
    foreach ($binding in @(Get-WebBinding -Name $site)) {
        if ($binding.protocol -ne 'https' -or $binding.bindingInformation -ne "*:${port}:$($Ctx.Iis.HostName)") {
            Remove-WebBinding -Name $site -BindingInformation $binding.bindingInformation -Protocol $binding.protocol
        }
    }
    if (-not (Get-WebBinding -Name $site -Protocol https | Where-Object { $_.bindingInformation -eq "*:${port}:$($Ctx.Iis.HostName)" })) {
        New-WebBinding -Name $site -Protocol https -Port $port -HostHeader $Ctx.Iis.HostName | Out-Null
    }
    $sslPath = "IIS:\SslBindings\0.0.0.0!$port"
    if (Test-Path $sslPath) { Remove-Item $sslPath -Force }
    (Get-WebBinding -Name $site -Protocol https | Select-Object -First 1).AddSslCertificate($Thumbprint, 'My')
    Set-WebConfigurationProperty -PSPath "IIS:\Sites\$site" -Filter 'system.webServer/security/requestFiltering/requestLimits' -Name maxAllowedContentLength -Value 10485760
}

function Set-HttpsCertificate {
    param($Ctx)
    $selector = $Ctx.Iis.Certificate
    $certs = @(Get-ChildItem Cert:\LocalMachine\My)
    if (-not (Test-IsAuto $selector) -and $selector -ne 'self-signed') {
        $thumb = ($selector -replace '\s', '').ToUpperInvariant()
        $cert = $certs | Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
        if (-not $cert) {
            Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked -WhatHappened "Certificado $thumb não encontrado em LocalMachine\My." -Impact 'Não há HTTPS para o site.' -HowToFix 'Importe o certificado (com chave privada) no repositório do computador ou use iis.certificate = "auto".'
        }
        return $cert.Thumbprint
    }
    if ($selector -ne 'self-signed') {
        $best = Select-BestCertificate -Certificates $certs -HostName $Ctx.Iis.HostName
        if ($best) {
            Write-Log "Certificado HTTPS escolhido: $($best.Subject) (vence em $($best.NotAfter.ToString('yyyy-MM-dd')))."
            return $best.Thumbprint
        }
    }
    if ($AllowSelfSigned -or $selector -eq 'self-signed') {
        Write-Log "Gerando certificado AUTOASSINADO para $($Ctx.Iis.HostName). Use somente em piloto: os navegadores exibirão aviso." 'WARN'
        $cert = New-SelfSignedCertificate -DnsName $Ctx.Iis.HostName -CertStoreLocation Cert:\LocalMachine\My -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2) -FriendlyName 'Azul Nexus (autoassinado)'
        return $cert.Thumbprint
    }
    Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked `
        -WhatHappened "Nenhum certificado válido com o nome '$($Ctx.Iis.HostName)' e chave privada foi encontrado em LocalMachine\My." `
        -Impact 'Sem certificado não há HTTPS e o site não sobe.' `
        -HowToFix 'Solicite um certificado de servidor à AC corporativa com esse nome DNS, informe o thumbprint em iis.certificate, ou use -AllowSelfSigned (somente piloto).'
}

function Set-FolderPermissions {
    param($Ctx)
    Write-Log 'Aplicando permissões de pastas...' 'STEP'
    $web = $Ctx.Accounts.WebAccount
    $worker = $Ctx.Accounts.WorkerAccount
    $admins = '*S-1-5-32-544:(OI)(CI)F'
    $system = '*S-1-5-18:(OI)(CI)F'

    # Binários: somente leitura para os serviços.
    Grant-Acl -Path $Ctx.InstallDir -Grants @($admins, $system, "${web}:(OI)(CI)RX", "${worker}:(OI)(CI)RX", '*S-1-5-32-545:(OI)(CI)RX')

    Grant-Acl -Path $Ctx.DataDir -ResetInheritance -Grants @($admins, $system, "${web}:(RX)", "${worker}:(RX)")
    Grant-Acl -Path (Join-Path $Ctx.DataDir 'logs') -ResetInheritance -Grants @($admins, $system, "${web}:(OI)(CI)M", "${worker}:(OI)(CI)M")
    Grant-Acl -Path (Join-Path $Ctx.DataDir 'config') -ResetInheritance -Grants @($admins, $system, "${web}:(OI)(CI)M", "${worker}:(OI)(CI)RX")
    Grant-Acl -Path (Join-Path $Ctx.DataDir 'keys') -ResetInheritance -Grants @($admins, $system, "${web}:(OI)(CI)M")
    Grant-Acl -Path (Join-Path $Ctx.DataDir 'backups') -ResetInheritance -Grants @($admins, $system)
    Grant-Acl -Path (Join-Path $Ctx.DataDir 'scripts') -ResetInheritance -Grants @($admins, $system)
}

function Set-Firewall {
    param($Ctx)
    if (-not $Ctx.Iis.OpenFirewall) { return }
    $display = 'Azul Nexus HTTPS'
    Get-NetFirewallRule -DisplayName $display -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $display -Direction Inbound -Protocol TCP -LocalPort $Ctx.Iis.Port -Action Allow -Profile Domain,Private | Out-Null
    $script:Undo.Push({ Get-NetFirewallRule -DisplayName 'Azul Nexus HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule })
}

function Initialize-Database {
    param($Ctx, $Previous)
    Write-Log 'Configurando o banco do Nexus...' 'STEP'
    $env:NEXUS_DATA_DIR = $Ctx.DataDir

    $resolvedPath = Join-Path $Ctx.DataDir 'config\install.resolved.json'
    ConvertTo-Json (ConvertTo-ResolvedInstallFile $Ctx) -Depth 8 | Set-Content -Path $resolvedPath -Encoding UTF8

    $passwordSet = $false
    if ($Ctx.Database.Provider -eq 'PostgreSql') {
        $plain = ConvertTo-PlainText $DatabasePassword
        if (-not $plain) { $plain = $env:NEXUS_DB_PASSWORD }
        if (-not $plain) {
            if ([Environment]::UserInteractive) { $plain = ConvertTo-PlainText (Read-Host -Prompt 'Senha do usuário do PostgreSQL' -AsSecureString) }
        }
        if (-not $plain) {
            Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked -WhatHappened 'Senha do PostgreSQL não informada.' -Impact 'O Nexus não consegue conectar ao banco.' -HowToFix 'Use -DatabasePassword (SecureString) ou a variável de ambiente NEXUS_DB_PASSWORD. A senha é guardada protegida por DPAPI, nunca em texto claro.'
        }
        $env:NEXUS_DB_PASSWORD = $plain
        $passwordSet = $true
    }
    try {
        Invoke-Nexusctl -Arguments @('configure', '--from', $resolvedPath) | Out-Null
    } finally {
        if ($passwordSet) { Remove-Item Env:\NEXUS_DB_PASSWORD -ErrorAction SilentlyContinue }
    }

    if ($Ctx.Database.Provider -eq 'SqlServer') {
        $dbAccounts = @($Ctx.Accounts.WebAccount, $Ctx.Accounts.WorkerAccount)
        $grant = Invoke-Nexusctl -Arguments (New-AccountArguments -Command 'db-grant' -Accounts $dbAccounts) -AllowFailure
        if ($grant.ExitCode -ne 0) {
            $file = Join-Path $Ctx.DataDir 'scripts\nexus-db-grant.sql'
            Invoke-Nexusctl -Arguments (New-AccountArguments -Command 'db-grant-script' -Accounts $dbAccounts -Extra @('--output', $file)) -AllowFailure | Out-Null
            $script:Pending.Add("DBA: executar $file (cria o banco '$($Ctx.Database.Name)' e dá acesso às contas dos serviços) e depois 'nexusctl migrate'.")
            return
        }
    }

    if ($Previous -and -not $SkipBackup) {
        Write-Log 'Atualização: fazendo backup do banco antes de migrar...' 'STEP'
        $backup = Invoke-Nexusctl -Arguments @('backup') -AllowFailure
        if ($backup.ExitCode -ne 0) {
            Stop-Install -ExitCode $script:ExitCodes.FailedRolledBack -WhatHappened 'O backup do banco antes da migração falhou.' -Impact 'Para não arriscar os dados, a atualização foi cancelada e a versão anterior foi mantida.' -HowToFix 'Peça ao DBA um backup do banco do Nexus e rode com -SkipBackup, ou corrija a permissão de backup.'
        }
    }

    $migrate = Invoke-Nexusctl -Arguments @('migrate') -AllowFailure
    if ($migrate.ExitCode -ne 0) {
        if ($Previous) {
            Stop-Install -ExitCode $script:ExitCodes.FailedRolledBack -WhatHappened 'A migração do banco falhou.' -Impact 'A versão anterior dos binários foi restaurada. O backup feito antes da migração está em <dados>\backups e no SQL Server/PostgreSQL.' -HowToFix 'Veja o log da instalação, corrija a causa e rode de novo; se necessário restaure o backup.'
        }
        $file = Join-Path $Ctx.DataDir 'scripts\nexus-db-migrations.sql'
        Invoke-Nexusctl -Arguments @('db-script', '--output', $file) -AllowFailure | Out-Null
        $script:Pending.Add("DBA: aplicar $file no banco '$($Ctx.Database.Name)' (sua conta não pôde criar as tabelas).")
    }
}

function Grant-SccmAccess {
    param($Ctx)
    if (-not $Ctx.Sccm.SqlServer -or $Ctx.DemoMode) { return }
    if ($Ctx.Sccm.GrantViewAccess -eq 'never') {
        $script:Pending.Add('SCCM: concessão de leitura desativada (grantViewAccess = never). Gere o script com: nexusctl sccm-grant-script --account "' + $Ctx.Accounts.WorkerAccount + '".')
        return
    }
    Write-Log 'Concedendo leitura nas views do SCCM...' 'STEP'
    $account = $Ctx.Accounts.WorkerAccount
    $result = Invoke-Nexusctl -Arguments @('sccm-grant', '--account', $account) -AllowFailure
    if ($result.ExitCode -ne 0) {
        $grantFile = Join-Path $Ctx.DataDir 'scripts\sccm-grant.sql'
        $revokeFile = Join-Path $Ctx.DataDir 'scripts\sccm-revoke.sql'
        Invoke-Nexusctl -Arguments @('sccm-grant-script', '--account', $account, '--output', $grantFile) -AllowFailure | Out-Null
        Invoke-Nexusctl -Arguments @('sccm-grant-script', '--account', $account, '--revoke', '--output', $revokeFile) -AllowFailure | Out-Null
        $script:Pending.Add("SCCM/DBA: executar $grantFile no SQL do site (papel somente leitura; reversão em $revokeFile).")
    }
}

function Wait-Healthy {
    param($Ctx, [int]$TimeoutSeconds = 120)
    # O site só aceita TLS 1.2; o .NET Framework do Windows PowerShell 5.1 pode usar um padrão mais antigo.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    # Cliente que aceita o certificado: a chamada é local (127.0.0.1) e o certificado é do nome público.
    # Callback em C# porque um scriptblock não roda em threads sem runspace (Windows PowerShell 5.1).
    Add-Type -AssemblyName System.Net.Http
    if (-not ('NexusLocalProbe' -as [type])) {
        Add-Type -ReferencedAssemblies 'System.Net.Http' -TypeDefinition @"
using System.Net.Http;
public static class NexusLocalProbe
{
    public static HttpClient CreateClient()
    {
        HttpClientHandler handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true;
        return new HttpClient(handler);
    }
}
"@
    }
    $client = [NexusLocalProbe]::CreateClient()
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $statusCode = $null; $body = ''; $errorText = ''
    while ((Get-Date) -lt $deadline) {
        $statusCode = $null; $body = ''; $errorText = ''
        try {
            $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "https://127.0.0.1:$($Ctx.Iis.Port)/healthz")
            $request.Headers.Host = "$($Ctx.Iis.HostName):$($Ctx.Iis.Port)"
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
            $statusCode = [int]$response.StatusCode
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        } catch {
            $inner = $_.Exception
            while ($inner.InnerException) { $inner = $inner.InnerException }
            $errorText = $inner.Message
        }
        $verdict = Resolve-HealthProbe -StatusCode $statusCode -Body $body -ErrorText $errorText
        # Resposta definitiva (ok, banco, migrações, configuração): não adianta esperar mais.
        if ($verdict.Ok -or ($body -match '"status"')) { break }
        Start-Sleep -Seconds 3
    }
    return [pscustomobject]@{ Verdict = $verdict; StatusCode = $statusCode; Body = $body; Error = $errorText }
}

# Coleta, no log da instalação, o que normalmente explica um site que não sobe (sem segredos).
function Write-SiteDiagnostics {
    param($Ctx)
    Write-Log '--- Diagnóstico do site ---' 'WARN'
    try {
        Import-Module WebAdministration -ErrorAction Stop
        $pool = $Ctx.Iis.AppPoolName; $site = $Ctx.Iis.SiteName
        Write-Log ("Pool '{0}': {1}; identidade: {2}" -f $pool, (Get-WebAppPoolState -Name $pool).Value, (Get-ItemProperty "IIS:\AppPools\$pool" -Name processModel.userName).Value)
        Write-Log ("Site '{0}': {1}; bindings: {2}" -f $site, (Get-WebsiteState -Name $site).Value, ((Get-WebBinding -Name $site | ForEach-Object { "$($_.protocol) $($_.bindingInformation)" }) -join '; '))
    } catch { Write-Log "Não foi possível ler o estado do IIS: $($_.Exception.Message)" 'WARN' }

    $logs = Join-Path $Ctx.DataDir 'logs'
    $webLog = Get-ChildItem $logs -Filter 'web-*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($webLog) {
        Write-Log "Últimas linhas de $($webLog.Name):" 'WARN'
        Get-Content $webLog.FullName -Tail 8 -ErrorAction SilentlyContinue | ForEach-Object { Write-Log ('  ' + $_.Substring(0, [math]::Min(500, $_.Length))) 'WARN' }
    } else {
        Write-Log "Nenhum web-*.json em ${logs}: o aplicativo nem chegou a iniciar (veja o Log de Eventos abaixo)." 'WARN'
    }

    try {
        $rapid = (Get-ItemProperty "IIS:\AppPools\$($Ctx.Iis.AppPoolName)" -Name failure.rapidFailProtection).Value
        Write-Log "Rapid-Fail Protection do pool: $rapid (ligado: o pool para sozinho depois de falhas repetidas do processo)." 'WARN'
    } catch { }
    # Pool parado por logon/identidade grava em System (origem WAS); falha do aplicativo grava em Application.
    foreach ($source in @(
            @{ Log = 'System'; Pattern = 'WAS|W3SVC|IIS' },
            @{ Log = 'Application'; Pattern = 'AspNetCore|\.NET Runtime|Application Error|Azul Nexus|IIS' })) {
        try {
            $events = Get-WinEvent -FilterHashtable @{ LogName = $source.Log; Level = 1, 2, 3; StartTime = (Get-Date).AddMinutes(-20) } -MaxEvents 60 -ErrorAction Stop |
                Where-Object { $_.ProviderName -match $source.Pattern } | Select-Object -First 8
            if ($events) {
                Write-Log "Eventos recentes no Log de Eventos ($($source.Log)):" 'WARN'
                foreach ($e in $events) { Write-Log ("  [{0}] {1} (ID {2}): {3}" -f $e.TimeCreated.ToString('HH:mm:ss'), $e.ProviderName, $e.Id, ($e.Message -replace '\s+', ' ').Substring(0, [math]::Min(700, $e.Message.Length))) 'WARN' }
            } else {
                Write-Log "Nenhum evento relevante em $($source.Log) nos últimos 20 minutos." 'WARN'
            }
        } catch { Write-Log "Não foi possível ler o log $($source.Log): $($_.Exception.Message)" 'WARN' }
    }
    Write-Log '--- Fim do diagnóstico ---' 'WARN'
}

#endregion

# Pede a senha da conta única (parâmetro, NEXUS_SERVICE_PASSWORD ou console) e a valida no domínio
# antes de qualquer alteração. A senha fica só em memória (SecureString).
function Get-ServicePassword {
    param([string]$Account)
    $secure = $ServiceAccountPassword
    if (-not $secure -and $env:NEXUS_SERVICE_PASSWORD) {
        $secure = ConvertTo-SecureString $env:NEXUS_SERVICE_PASSWORD -AsPlainText -Force
        Remove-Item Env:\NEXUS_SERVICE_PASSWORD -ErrorAction SilentlyContinue
    }
    if (-not $secure -and [Environment]::UserInteractive) {
        $secure = Read-Host -Prompt "Senha da conta $Account" -AsSecureString
    }
    if (-not $secure) {
        Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked -WhatHappened "A senha da conta $Account não foi informada." -Impact 'Nada foi alterado.' -HowToFix 'Rode no console para digitar a senha, ou use -ServiceAccountPassword (SecureString) ou a variável NEXUS_SERVICE_PASSWORD.'
    }
    $domain, $user = if ($Account.Contains('\')) { $Account.Split('\', 2) } else { $null, $Account }
    if ($domain) {
        try {
            Add-Type -AssemblyName System.DirectoryServices.AccountManagement
            $context = New-Object System.DirectoryServices.AccountManagement.PrincipalContext([System.DirectoryServices.AccountManagement.ContextType]::Domain, $domain)
            if (-not $context.ValidateCredentials($user, (ConvertTo-PlainText $secure))) {
                Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked -WhatHappened "A senha da conta $Account foi recusada pelo domínio (senha incorreta, conta bloqueada ou desabilitada)." -Impact 'Nada foi alterado.' -HowToFix 'Confira a senha e o estado da conta no AD e rode de novo.'
            }
        } catch [System.Management.Automation.RuntimeException] { throw } catch {
            Write-Log "Não foi possível validar a senha no domínio ($($_.Exception.Message)). Seguindo; o Windows validará ao configurar o serviço." 'WARN'
        }
    }
    return $secure
}

function Invoke-Install {
    if (-not $HostingBundleInstaller) {
        $bundled = Get-ChildItem (Join-Path $script:PackageRoot 'prereq') -Filter 'dotnet-hosting*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($bundled) { $script:HostingBundleInstaller = $bundled.FullName }
    }
    $answersPath = if ($Answers) { $Answers } else { Join-Path $PSScriptRoot 'install.json' }
    if (-not (Test-Path $answersPath)) {
        Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked -WhatHappened "Arquivo de respostas não encontrado: $answersPath." -Impact 'Não há como saber o banco e as demais escolhas.' -HowToFix 'Copie install.sample.json para install.json, ajuste database.server e rode de novo (ou use -Answers <arquivo>).'
    }
    $raw = Get-Content $answersPath -Raw | ConvertFrom-Json
    $ctx = Resolve-Context -Raw $raw
    if (-not $LogPath) { $LogPath = Join-Path $ctx.DataDir ("logs\install-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    Initialize-Log -Path $LogPath
    Write-Log "Azul Nexus $($ctx.Version): início ($($ctx.PublicUrl))." 'STEP'

    $checks = Test-Environment -Ctx $ctx
    Show-Checks $checks
    if (@($checks | Where-Object Status -eq 'Bloqueio').Count -gt 0) {
        Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked -WhatHappened 'Há bloqueios nas verificações do ambiente.' -Impact 'Nada foi alterado no servidor.' -HowToFix 'Resolva os itens marcados como Bloqueio (veja "Como resolver" acima) e rode de novo.'
    }
    if ($DetectOnly) {
        Write-Log 'Modo -DetectOnly: nada foi alterado.' 'STEP'
        return
    }

    if ($ctx.Accounts.Mode -eq 'SharedAccount') { $script:ServicePassword = Get-ServicePassword -Account $ctx.Accounts.Account }

    $previous = Get-CurrentInstall
    if ($previous) { Write-Log "Instalação existente: versão $($previous.Version). Será $(if ($previous.Version -eq $ctx.Version) { 'reparada' } else { 'atualizada' })." }

    Install-HostingBundle
    Initialize-DataFolders -Ctx $ctx
    Publish-Files -Ctx $ctx -Previous $previous
    Set-RegistryInfo -Ctx $ctx
    Set-ServiceAccountRights -Ctx $ctx
    Test-AccountLogons -Ctx $ctx
    Set-WorkerService -Ctx $ctx
    $thumbprint = Set-HttpsCertificate -Ctx $ctx
    Set-IisSite -Ctx $ctx -Thumbprint $thumbprint
    Set-FolderPermissions -Ctx $ctx
    Set-Firewall -Ctx $ctx

    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $appDir = Join-Path $ctx.InstallDir 'app'
    if (($machinePath -split ';') -notcontains $appDir) {
        [Environment]::SetEnvironmentVariable('Path', ($machinePath.TrimEnd(';') + ';' + $appDir), 'Machine')
    }

    Initialize-Database -Ctx $ctx -Previous $previous
    Grant-SccmAccess -Ctx $ctx

    Write-Log 'Iniciando serviços...' 'STEP'
    $siteDir = Join-Path $ctx.InstallDir 'web'
    Remove-Item (Join-Path $siteDir 'app_offline.htm') -Force -ErrorAction SilentlyContinue
    Start-Service -Name 'AzulNexus.Worker'
    Import-Module WebAdministration
    if ((Get-WebAppPoolState -Name $ctx.Iis.AppPoolName).Value -ne 'Started') { Start-WebAppPool -Name $ctx.Iis.AppPoolName }
    if ((Get-WebsiteState -Name $ctx.Iis.SiteName).Value -ne 'Started') { Start-Website -Name $ctx.Iis.SiteName }

    $probe = Wait-Healthy -Ctx $ctx
    if (-not $probe.Verdict.Ok) {
        Write-SiteDiagnostics -Ctx $ctx
        $received = if ($null -ne $probe.StatusCode) { "Resposta: HTTP $($probe.StatusCode) $($probe.Body)" } else { "Erro: $($probe.Error)" }
        Stop-Install -ExitCode $script:ExitCodes.InstalledNotHealthy `
            -WhatHappened "$($probe.Verdict.Summary) ($received)" `
            -Impact 'A interface não está disponível. A instalação foi MANTIDA (nada foi desfeito) para você poder investigar.' `
            -HowToFix $probe.Verdict.HowToFix
    }
    Write-Log "Health check do site: $($probe.Body)"

    Write-Log 'Rodando as verificações no Worker (com a conta do serviço)...' 'STEP'
    $test = Invoke-Nexusctl -Arguments @('test', '--timeout', '120') -AllowFailure
    if ($test.ExitCode -ne 0) { $script:Pending.Add('Verificações com erro: veja o resultado acima ou em Saúde (nexusctl test).') }
    Invoke-Nexusctl -Arguments @('collect', 'all') -AllowFailure | Out-Null
    Write-Log 'Primeira coleta solicitada ao Worker.'

    $codeOutput = Invoke-Nexusctl -Arguments @('setup-code') -AllowFailure -Quiet
    $setupCode = ($codeOutput.Output | Select-String -Pattern '[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}' | Select-Object -First 1).Matches.Value

    Write-Log "Concluído. Endereço: $($ctx.PublicUrl)" 'STEP'
    foreach ($item in $script:Pending) { Write-Log "PENDENTE: $item" 'WARN' }
    # O código não vai para o arquivo de log (é um segredo de uso único).
    if ($setupCode) {
        [Console]::WriteLine("Código de configuração (uso único, válido por 24 horas): $setupCode")
        [Console]::WriteLine("No próprio servidor o código não é necessário. Gere outro com: nexusctl setup-code")
    }
    if (-not $NoOpenBrowser -and [Environment]::UserInteractive) { Start-Process $ctx.PublicUrl -ErrorAction SilentlyContinue }
}

$exitCode = $script:ExitCodes.Success
try {
    if (-not (Test-Administrator)) {
        Stop-Install -ExitCode $script:ExitCodes.PrerequisiteBlocked -WhatHappened 'O script não está em execução como administrador.' -Impact 'Nada foi alterado.' -HowToFix 'Abra um PowerShell com "Executar como administrador" e rode de novo.'
    }
    Invoke-Install
    if ($script:RebootRequired) { $exitCode = $script:ExitCodes.RebootRequired }
} catch {
    $exitCode = if ($_.Exception.Data.Contains('ExitCode')) { [int]$_.Exception.Data['ExitCode'] } else { $script:ExitCodes.FailedRolledBack }
    $message = if ($_.Exception.Data.Contains('ExitCode')) { $_.Exception.Message } else { "O que aconteceu: $($_.Exception.Message)`nImpacto: a instalação foi interrompida.`nComo resolver: veja o log e rode o script de novo; ele é idempotente." }
    Write-Log $message 'ERROR'
    if ($exitCode -eq $script:ExitCodes.FailedRolledBack) {
        while ($script:Undo.Count -gt 0) {
            $action = $script:Undo.Pop()
            try { & $action } catch { Write-Log "Falha ao desfazer uma etapa: $($_.Exception.Message)" 'WARN' }
        }
        Write-Log 'Desfazer concluído.' 'WARN'
    }
}
exit $exitCode
