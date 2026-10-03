# Funções compartilhadas pelos scripts de implantação do Azul Nexus.
# Compatível com Windows PowerShell 5.1 (padrão do Windows Server) e PowerShell 7.
# As funções "puras" (sem efeito no servidor) são testadas em deploy/tests/Common.Tests.ps1.

$script:ExitCodes = @{
    Success             = 0
    RebootRequired      = 3010
    PrerequisiteBlocked = 10
    FailedRolledBack    = 20
    InstalledNotHealthy = 30
}

# Portas que o Nexus nunca deve usar: SCCM (management point / distribution point), WSUS e sites padrão.
$script:ReservedPorts = @(80, 443, 8530, 8531, 10123)

$script:LogFile = $null

function Initialize-Log {
    param([Parameter(Mandatory)][string]$Path)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $script:LogFile = $Path
}

# O log nunca recebe segredos: senhas e o código de configuração não passam por aqui.
function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '[{0}] [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    if ($script:LogFile) { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 }
    switch ($Level) {
        'WARN'  { Write-Host $line -ForegroundColor Yellow }
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'STEP'  { Write-Host $line -ForegroundColor Cyan }
        default { Write-Host $line }
    }
}

function New-InstallException {
    param([int]$ExitCode, [string]$Message)
    $ex = New-Object System.Exception $Message
    $ex.Data['ExitCode'] = $ExitCode
    return $ex
}

# Toda falha segue o padrão do produto: o que aconteceu, impacto e como resolver.
function Stop-Install {
    param(
        [Parameter(Mandatory)][int]$ExitCode,
        [Parameter(Mandatory)][string]$WhatHappened,
        [Parameter(Mandatory)][string]$Impact,
        [Parameter(Mandatory)][string]$HowToFix
    )
    $message = "O que aconteceu: $WhatHappened`nImpacto: $Impact`nComo resolver: $HowToFix"
    throw (New-InstallException -ExitCode $ExitCode -Message $message)
}

# Executa um programa nativo e devolve ExitCode e Output. No Windows PowerShell 5.1, com $ErrorActionPreference = 'Stop',
# qualquer linha escrita em stderr (2>&1) vira exceção e derrubaria o script mesmo com -AllowFailure;
# por isso o preference é 'Continue' só durante a chamada.
function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments, [switch]$AllowFailure)
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $FilePath @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($code -ne 0 -and -not $AllowFailure) {
        Stop-Install -ExitCode $script:ExitCodes.FailedRolledBack `
            -WhatHappened "O comando '$([IO.Path]::GetFileName($FilePath)) $($Arguments -join ' ')' falhou com código $code. $($output -join ' ')" `
            -Impact 'A instalação foi interrompida.' `
            -HowToFix 'Corrija a causa indicada e rode o script de novo; ele é idempotente.'
    }
    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

# Interpreta a resposta (ou a falta dela) do /healthz do site e diz, em português, o que aconteceu e como resolver.
function Resolve-HealthProbe {
    param($StatusCode, [string]$Body, [string]$ErrorText)
    $status = $null
    if ($Body -match '"status"\s*:\s*"([^"]+)"') { $status = $Matches[1] }

    if ($StatusCode -eq 200 -and $status -eq 'ok') {
        return [pscustomobject]@{ Ok = $true; Summary = 'O site respondeu e o banco está na versão atual.'; HowToFix = '' }
    }
    if ($status -eq 'database-unreachable') {
        return [pscustomobject]@{ Ok = $false
            Summary = 'O site subiu, mas não consegue conectar ao banco do Nexus.'
            HowToFix = 'Confirme que a conta dos serviços (svc.sccm) tem acesso ao banco do Nexus (db_datareader e db_datawriter) e que o servidor SQL está acessível. Depois: Restart-WebAppPool AzulNexus.' }
    }
    if ($status -eq 'migrations-pending') {
        return [pscustomobject]@{ Ok = $false
            Summary = 'O site subiu, mas as tabelas do banco do Nexus estão desatualizadas.'
            HowToFix = 'Rode "nexusctl migrate" como administrador (ou aplique o script de <dados>\scripts\nexus-db-migrations.sql).' }
    }
    if ($status -eq 'unconfigured') {
        return [pscustomobject]@{ Ok = $false
            Summary = 'O site subiu, mas não encontrou a configuração (nexus.json).'
            HowToFix = 'Confirme que a conta do pool tem leitura em <dados>\config e rode o instalador de novo.' }
    }
    if ($null -ne $StatusCode) {
        $code = [int]$StatusCode
        if ($code -eq 404) {
            return [pscustomobject]@{ Ok = $false
                Summary = 'O IIS respondeu 404: o endereço não bate com o site do Nexus.'
                HowToFix = 'Confira o nome em iis.hostName e o binding HTTPS do site no Gerenciador do IIS.' }
        }
        if ($code -ge 500) {
            return [pscustomobject]@{ Ok = $false
                Summary = "O IIS respondeu HTTP ${code}: o aplicativo do Nexus não iniciou."
                HowToFix = 'Veja o estado do pool AzulNexus (parado = senha da conta recusada ou conta sem permissão), o Log de Eventos (Application: IIS AspNetCore Module V2 e .NET Runtime) e <dados>\logs\web-*.json.' }
        }
        return [pscustomobject]@{ Ok = $false
            Summary = "O site respondeu HTTP $code, resposta inesperada do /healthz."
            HowToFix = 'Veja <dados>\logs\web-*.json e o Log de Eventos.' }
    }
    $detail = if ($ErrorText) { $ErrorText } else { 'sem resposta' }
    return [pscustomobject]@{ Ok = $false
        Summary = "Não foi possível conectar ao site por HTTPS ($detail)."
        HowToFix = 'Confirme que o site e o pool estão iniciados, que há certificado no binding da porta e que o servidor aceita TLS 1.2. Veja o diagnóstico abaixo.' }
}

# Traduz o código de erro do LogonUser (Win32) para o que aconteceu e como resolver.
# Batch = logon "como trabalho em lote" (exigido pelo pool do IIS); Service = "como serviço" (Worker).
function Resolve-LogonFailure {
    param([int]$Code, [ValidateSet('Batch', 'Service')][string]$LogonType, [string]$Account)
    $what = if ($LogonType -eq 'Batch') { 'fazer logon como trabalho em lote (direito exigido pelo pool do IIS)' } else { 'fazer logon como serviço (direito exigido pelo Worker)' }
    $policy = if ($LogonType -eq 'Batch') { "'Fazer logon como um trabalho em lote' (Log on as a batch job)" } else { "'Fazer logon como um serviço' (Log on as a service)" }
    $deny = if ($LogonType -eq 'Batch') { "'Negar logon como um trabalho em lote'" } else { "'Negar logon como um serviço'" }
    switch ($Code) {
        1385 { return [pscustomobject]@{ Definitive = $true
            Summary = "A conta $Account não tem permissão para $what neste servidor (erro 1385)."
            HowToFix = "No servidor, abra secpol.msc › Diretivas Locais › Atribuição de Direitos de Usuário: inclua a conta em $policy e confirme que ela NÃO está em $deny. Se essas diretivas vêm de GPO (gpresult /h gpo.html), a concessão local é sobrescrita: peça à equipe de AD para ajustar a GPO." } }
        1326 { return [pscustomobject]@{ Definitive = $true; Summary = "Usuário ou senha incorretos para $Account (erro 1326)."; HowToFix = 'Confira a senha da conta e rode o instalador de novo.' } }
        1330 { return [pscustomobject]@{ Definitive = $true; Summary = "A senha da conta $Account expirou (erro 1330)."; HowToFix = 'Redefina a senha da conta no AD e rode o instalador de novo.' } }
        1331 { return [pscustomobject]@{ Definitive = $true; Summary = "A conta $Account está desabilitada (erro 1331)."; HowToFix = 'Habilite a conta no AD e rode o instalador de novo.' } }
        1909 { return [pscustomobject]@{ Definitive = $true; Summary = "A conta $Account está bloqueada (erro 1909)."; HowToFix = 'Desbloqueie a conta no AD (verifique se algum serviço antigo usa a senha errada) e rode o instalador de novo.' } }
        default { return [pscustomobject]@{ Definitive = $false
            Summary = "Não foi possível confirmar o logon de $Account para $what (erro Win32 $Code)."
            HowToFix = 'Veja o Log de Eventos (System, origem WAS) depois da instalação para o motivo exato.' } }
    }
}

function New-Check {
    param([string]$Name, [ValidateSet('OK', 'Atenção', 'Bloqueio')][string]$Status, [string]$Message, [string]$Fix = '')
    return [pscustomobject]@{ Name = $Name; Status = $Status; Message = $Message; Fix = $Fix }
}

# Lê "a.b.c" de um objeto vindo de ConvertFrom-Json; devolve $Default se ausente, nulo ou vazio.
function Get-Setting {
    param($Object, [string]$Path, $Default = $null)
    $current = $Object
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $current) { return $Default }
        $current = $current.$part
    }
    if ($null -eq $current) { return $Default }
    if ($current -is [string] -and $current.Trim().Length -eq 0) { return $Default }
    return $current
}

function Test-IsAuto {
    param($Value)
    return ($null -eq $Value) -or (($Value -is [string]) -and ($Value.Trim() -ieq 'auto'))
}

function Get-SqlHostName {
    param([string]$Server)
    if ([string]::IsNullOrWhiteSpace($Server)) { return '' }
    $name = ($Server -split '[\\,]')[0].Trim()
    if ($name -like 'tcp:*') { $name = $name.Substring(4) }
    return $name
}

function Test-LocalSqlServer {
    param([string]$Server, [string]$ComputerName = $env:COMPUTERNAME)
    $name = Get-SqlHostName $Server
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    if ($name -in @('.', '(local)', 'localhost', '127.0.0.1')) { return $true }
    return ($name -ieq $ComputerName) -or ($name -like "$ComputerName.*")
}

function Test-SameSqlInstance {
    param([string]$A, [string]$B)
    if ([string]::IsNullOrWhiteSpace($A) -or [string]::IsNullOrWhiteSpace($B)) { return $false }
    $normalize = {
        param($s)
        $parts = $s.Trim().ToLowerInvariant() -split '\\', 2
        $hostName = Get-SqlHostName $parts[0]
        $hostName = ($hostName -split '\.')[0]
        if ($hostName -in @('.', '(local)', 'localhost', '127.0.0.1')) { $hostName = $env:COMPUTERNAME.ToLowerInvariant() }
        $instance = if ($parts.Count -gt 1) { $parts[1] } else { 'mssqlserver' }
        return "$hostName\$instance"
    }
    return (& $normalize $A) -eq (& $normalize $B)
}

function Get-SafeName {
    param([string]$Value)
    return ($Value -replace '[^A-Za-z0-9_]', '_')
}

# Maior volume local que não seja o do sistema; sem ele, usa ProgramData.
function Select-DataDirectory {
    param($Disks, [string]$SystemDrive = $env:SystemDrive, [string]$ProgramData = $env:ProgramData)
    $candidate = @($Disks) |
        Where-Object { $_.DeviceID -and ($_.DeviceID -ne $SystemDrive) -and $_.FreeSpace -gt 5GB } |
        Sort-Object FreeSpace -Descending |
        Select-Object -First 1
    if ($candidate) { return ($candidate.DeviceID + '\AzulNexus') }
    return ($ProgramData.TrimEnd('\') + '\AzulNexus')
}

function Test-CertificateMatchesHost {
    param($Certificate, [string]$HostName)
    $names = @()
    foreach ($entry in @($Certificate.DnsNameList)) {
        if ($null -ne $entry) {
            $text = if ($entry.Unicode) { $entry.Unicode } else { "$entry" }
            if ($text) { $names += $text }
        }
    }
    if ($names.Count -eq 0 -and $Certificate.Subject -match 'CN=([^,]+)') { $names += $Matches[1] }
    foreach ($name in $names) {
        $pattern = '^' + [regex]::Escape($name).Replace('\*', '[^.]+') + '$'
        if ($HostName -imatch $pattern) { return $true }
    }
    return $false
}

# Melhor certificado de servidor da máquina: com chave privada, válido, com o nome de acesso,
# emitido por AC (não autoassinado) e com a validade mais longa.
function Select-BestCertificate {
    param($Certificates, [string]$HostName, [datetime]$Now = (Get-Date), [int]$MinimumDaysLeft = 30)
    $serverAuth = '1.3.6.1.5.5.7.3.1'
    $valid = @($Certificates) | Where-Object {
        $_.HasPrivateKey -and $_.NotBefore -le $Now -and $_.NotAfter -gt $Now -and (
            (@($_.EnhancedKeyUsageList).Count -eq 0) -or
            (@($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq $serverAuth }).Count -gt 0))
    }
    $matching = @($valid | Where-Object { Test-CertificateMatchesHost -Certificate $_ -HostName $HostName })
    if ($matching.Count -eq 0) { return $null }
    $durable = @($matching | Where-Object { $_.NotAfter -gt $Now.AddDays($MinimumDaysLeft) })
    $pool = if ($durable.Count -gt 0) { $durable } else { $matching }
    return $pool |
        Sort-Object @{ Expression = { $_.Subject -ne $_.Issuer }; Descending = $true }, @{ Expression = { $_.NotAfter }; Descending = $true } |
        Select-Object -First 1
}

function New-NexusConnectionString {
    param([ValidateSet('SqlServer', 'PostgreSql')][string]$Provider, $Database, [bool]$TrustServerCertificate = $false)
    $name = Get-Setting $Database 'name' 'AzulNexus'
    if ($Provider -eq 'SqlServer') {
        $server = Get-Setting $Database 'server' ''
        $trust = if ($TrustServerCertificate) { 'True' } else { 'False' }
        return "Server=$server;Database=$name;Integrated Security=true;Encrypt=True;TrustServerCertificate=$trust;Application Name=Azul Nexus"
    }
    $pgHost = Get-Setting $Database 'server' ''
    $port = Get-Setting $Database 'port' 5432
    $user = Get-Setting $Database 'username' 'azulnexus'
    return "Host=$pgHost;Port=$port;Database=$name;Username=$user;SSL Mode=Require"
}

# DOMINIO\conta a partir de "conta" (sem domínio) ou "DOMINIO\conta"; UPN (conta@dominio) é mantido e
# convertido para DOMINIO\conta pelo instalador no servidor (precisa do AD).
function Resolve-AccountName {
    param([string]$Account, [string]$DefaultDomain = $env:USERDOMAIN)
    if ([string]::IsNullOrWhiteSpace($Account)) { return $null }
    $name = $Account.Trim()
    if ($name.Contains('\') -or $name.Contains('@')) { return $name }
    if ([string]::IsNullOrWhiteSpace($DefaultDomain)) { return $name }
    return "$DefaultDomain\$name"
}

# Contas dos serviços. Com -Account, o site (pool do IIS) e o Worker rodam sob UMA conta de domínio
# (ex.: svc.sccm), sem contas virtuais nem gMSA. Sem -Account vale o padrão da SPEC §5.2: conta virtual
# quando o SQL é local e gMSA quando é remoto (a conta virtual aparece na rede como a conta de computador).
function Resolve-ServiceAccounts {
    param(
        [string]$Account,
        [string]$WebGmsa,
        [string]$WorkerGmsa,
        [Parameter(Mandatory)][string]$PoolName,
        [ValidateSet('service', 'iis')][string]$WebHosting = 'service',
        [string]$Provider,
        [string]$NexusDbServer,
        [string]$SccmSqlServer,
        [bool]$SccmLive
    )
    if ($Account) {
        return [pscustomobject]@{
            Mode          = 'SharedAccount'
            Account       = $Account
            WebGmsa       = $null
            WorkerGmsa    = $null
            WebAccount    = $Account
            WorkerAccount = $Account
            Problems      = @()
        }
    }

    $problems = @()
    $nexusDbRemote = ($Provider -eq 'SqlServer') -and -not (Test-LocalSqlServer $NexusDbServer)
    $sccmRemote = $SccmLive -and -not (Test-LocalSqlServer $SccmSqlServer)

    if (($nexusDbRemote) -and -not $WebGmsa) {
        $problems += "O site (pool '$PoolName') acessa o banco do Nexus em '$NexusDbServer', que é remoto. Informe serviceIdentity.account (ex.: svc.sccm) ou serviceIdentity.webGmsa."
    }
    if (($nexusDbRemote -or $sccmRemote) -and -not $WorkerGmsa) {
        $remoteTarget = if ($sccmRemote) { "o SQL do site SCCM '$SccmSqlServer'" } else { "o banco do Nexus '$NexusDbServer'" }
        $problems += "O Worker acessa $remoteTarget, que é remoto. Informe serviceIdentity.account (ex.: svc.sccm) ou serviceIdentity.workerGmsa."
    }

    return [pscustomobject]@{
        Mode          = 'Default'
        Account       = $null
        WebGmsa       = $WebGmsa
        WorkerGmsa    = $WorkerGmsa
        WebAccount    = if ($WebGmsa) { $WebGmsa } elseif ($WebHosting -eq 'iis') { "IIS APPPOOL\$PoolName" } else { 'NT SERVICE\AzulNexus.Web' }
        WorkerAccount = if ($WorkerGmsa) { $WorkerGmsa } else { 'NT SERVICE\AzulNexus.Worker' }
        Problems      = $problems
    }
}

function Test-PortReserved {
    param([int]$Port)
    return $script:ReservedPorts -contains $Port
}

function Get-DefaultHostName {
    try { return [System.Net.Dns]::GetHostEntry('').HostName } catch { return $env:COMPUTERNAME }
}

# Arquivo com valores concretos (nenhum "auto") lido por 'nexusctl configure --from'.
function ConvertTo-ResolvedInstallFile {
    param($Context)
    $sccmMode = if ($Context.Sccm.SqlServer) { 'Live' } else { 'Disabled' }
    $adMode = if ($Context.ActiveDirectory.Domain) { 'Live' } else { 'Disabled' }
    $resolved = [ordered]@{
        installDir      = $Context.InstallDir
        dataDir         = $Context.DataDir
        database        = [ordered]@{ provider = $Context.Database.Provider; connectionString = $Context.Database.ConnectionString }
        sccm            = [ordered]@{
            mode                   = $sccmMode
            siteCode               = $Context.Sccm.SiteCode
            sqlServer              = $Context.Sccm.SqlServer
            database               = $Context.Sccm.Database
            trustServerCertificate = [bool]$Context.Sccm.TrustServerCertificate
        }
        activeDirectory = [ordered]@{
            mode        = $adMode
            domain      = $Context.ActiveDirectory.Domain
            server      = $Context.ActiveDirectory.Server
            searchBases = @($Context.ActiveDirectory.SearchBases)
            useLdaps    = [bool]$Context.ActiveDirectory.UseLdaps
        }
        publicUrl       = $Context.PublicUrl
        hosting         = $Context.Hosting
        httpsPort       = [int]$Context.Iis.Port
        certificateThumbprint = $Context.Iis.Thumbprint
        demoMode        = [bool]$Context.DemoMode
    }
    if ($Context.Collection) { $resolved['collection'] = $Context.Collection }
    return $resolved
}

# Escolha do banco do Nexus. Na mesma instância do SCCM: nunca dentro do banco do site (CM_xxx); em outro banco da
# instância só com allowSccmInstance = true (licenciamento confirmado), e mesmo assim com aviso de licença e carga.
function Test-NexusDatabaseChoice {
    param([string]$Provider, [string]$Server, [string]$Name, [string]$SccmServer, [string]$SccmDatabase, [bool]$AllowSccmInstance)
    $checks = @()
    if (-not $Server) {
        return @(New-Check 'Banco do Nexus' 'Bloqueio' 'Servidor do banco do Nexus não informado.' 'Preencha database.server (SQL Server ou PostgreSQL). O Nexus não cria banco embarcado.')
    }
    $sameInstance = ($Provider -eq 'SqlServer') -and (Test-SameSqlInstance $Server $SccmServer)
    if (-not $sameInstance) {
        return @(New-Check 'Banco do Nexus' 'OK' "$Provider em $Server, banco $Name.")
    }

    $isSiteDatabase = ($Name -ieq $SccmDatabase) -or ($Name -imatch '^CM_[A-Za-z0-9]{3}$')
    if ($isSiteDatabase) {
        return @(New-Check 'Banco do Nexus' 'Bloqueio' "O banco '$Name' é (ou parece ser) o banco do site do SCCM em $Server. O Nexus nunca grava dentro do banco do Configuration Manager." 'Use um banco novo e separado, por exemplo: "database": { "name": "AzulNexus" }.')
    }
    if (-not $AllowSccmInstance) {
        $json = '"database": { "provider": "SqlServer", "server": "' + $Server + '", "name": "' + $Name + '", "allowSccmInstance": true }'
        return @(New-Check 'Banco do Nexus' 'Bloqueio' "O banco do Nexus ficaria na mesma instância SQL do SCCM ($Server). O SQL Server que acompanha o Configuration Manager costuma ter licença restrita aos bancos do próprio SCCM." "Se o licenciamento da instância permite (confirme com quem cuida das licenças), libere no install.json com: $json. Caso contrário, use outra instância SQL ou PostgreSQL.")
    }
    return @(New-Check 'Banco do Nexus' 'Atenção' "Usando a instância SQL do SCCM ($Server) para o banco separado '$Name', por decisão do responsável (allowSccmInstance = true). Confirme que o licenciamento permite. As gravações do Nexus não passam pelos limites de proteção do SCCM (concorrência, janelas de pausa, recuo por CPU), que valem só para a leitura das views." 'Se possível, mova depois para outra instância: basta mudar database.server e rodar o instalador de novo (os dados antigos precisam ser migrados pelo DBA).')
}

# Argumentos do nexusctl com uma opção repetida por valor: --account A --account B [extras].
# Montado em função própria porque "@(...) + @(...)" solto na chamada vira argumentos separados no PowerShell.
function New-AccountArguments {
    param([Parameter(Mandatory)][string]$Command, [string[]]$Accounts, [string[]]$Extra = @())
    $arguments = New-Object System.Collections.Generic.List[string]
    $arguments.Add($Command)
    foreach ($account in @($Accounts | Where-Object { $_ } | Select-Object -Unique)) {
        $arguments.Add('--account')
        $arguments.Add($account)
    }
    foreach ($item in $Extra) { $arguments.Add($item) }
    return , $arguments.ToArray()
}

function Grant-Acl {
    param([string]$Path, [string[]]$Grants, [switch]$ResetInheritance)
    $arguments = @($Path)
    if ($ResetInheritance) { $arguments += '/inheritance:r' }
    foreach ($grant in $Grants) { $arguments += @('/grant:r', $grant) }
    $arguments += '/Q'
    Invoke-Native -FilePath 'icacls.exe' -Arguments $arguments | Out-Null
}

# A conta do serviço precisa ler a chave privada de certificados de LocalMachine\My usados por ele
# (HTTPS do Kestrel, certificados dos registros de aplicativo do Entra). O instalador roda como administrador;
# o serviço, não.
function Grant-CertificateKeyAccess {
    param([Parameter(Mandatory)][string]$Account, [Parameter(Mandatory)][string]$Thumbprint)
    $certificate = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    if (-not $certificate) { return }
    $keyFile = $null
    try {
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        if ($rsa -is [System.Security.Cryptography.RSACng]) {
            $name = $rsa.Key.UniqueName
            $keyFile = Get-ChildItem (Join-Path $env:ProgramData 'Microsoft\Crypto\Keys') -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
        } elseif ($rsa -and $rsa.CspKeyContainerInfo) {
            $name = $rsa.CspKeyContainerInfo.UniqueKeyContainerName
            $keyFile = Get-ChildItem (Join-Path $env:ProgramData 'Microsoft\Crypto\RSA\MachineKeys') -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
        }
    } catch { Write-Log "Não foi possível localizar a chave privada do certificado: $($_.Exception.Message)" 'WARN' }
    if ($keyFile) {
        Grant-Acl -Path $keyFile.FullName -Grants @("${Account}:R")
        Write-Log "Leitura da chave privada do certificado concedida a $Account."
    } else {
        Write-Log 'Não foi possível localizar o arquivo da chave privada (certificado de HSM/cartão ou chave de usuário). Se o serviço não conseguir usar o certificado, conceda leitura da chave à conta do serviço em certlm.msc › Gerenciar Chaves Privadas.' 'WARN'
    }
}

function ConvertTo-PlainText {
    param([securestring]$Secure)
    if (-not $Secure) { return $null }
    $ptr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}
