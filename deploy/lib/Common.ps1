# Funções compartilhadas pelos scripts de implantação do Azul Nexus.
# Compatível com Windows PowerShell 5.1 (padrão do Windows Server) e PowerShell 7.
# As funções "puras" (sem efeito no servidor) são testadas em deploy/tests/Common.Tests.ps1.

$script:ExitCodes = @{
    Success             = 0
    RebootRequired      = 3010
    PrerequisiteBlocked = 10
    FailedRolledBack    = 20
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
        WebAccount    = if ($WebGmsa) { $WebGmsa } else { "IIS APPPOOL\$PoolName" }
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

function ConvertTo-PlainText {
    param([securestring]$Secure)
    if (-not $Secure) { return $null }
    $ptr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}
