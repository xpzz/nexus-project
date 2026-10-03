# Provisionamento do Azul Nexus no Microsoft Entra ID / Intune (Microsoft Graph REST, sem módulos).
# Compatível com Windows PowerShell 5.1. Depende de Common.ps1 (Write-Log, Stop-Install).
#
# Cria (de forma idempotente) os registros "Azul Nexus – Coletor" e "Azul Nexus – Web", sobe os certificados públicos,
# concede o consentimento do administrador, cria as funções do aplicativo, exige atribuição e atribui o primeiro
# administrador. Nenhum segredo de cliente é criado: a autenticação do aplicativo é por certificado.

$script:GraphAppId = '00000003-0000-0000-c000-000000000000'
$script:GraphBase = 'https://graph.microsoft.com/v1.0'
# Aplicativo público "Microsoft Graph Command Line Tools": é o cliente do login do administrador (device code).
$script:AdminClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$script:AdminScopes = 'https://graph.microsoft.com/Application.ReadWrite.All https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All https://graph.microsoft.com/DelegatedPermissionGrant.ReadWrite.All https://graph.microsoft.com/Directory.Read.All https://graph.microsoft.com/User.Read.All'

$script:CollectorAppName = 'Azul Nexus – Coletor'
$script:WebAppName = 'Azul Nexus – Web'

# Permissões de aplicativo do Coletor (SPEC §4.5) e a chamada mínima que prova cada uma (SPEC §4.7).
$script:CollectorPermissions = @(
    @{ Name = 'DeviceManagementManagedDevices.Read.All'; Optional = $false; Probe = '/deviceManagement/managedDevices?$top=1&$select=id' },
    @{ Name = 'Device.Read.All'; Optional = $false; Probe = '/devices?$top=1&$select=id' },
    @{ Name = 'User.Read.All'; Optional = $false; Probe = '/users?$top=1&$select=id' },
    @{ Name = 'GroupMember.Read.All'; Optional = $false; Probe = '/groups?$top=1&$select=id' },
    @{ Name = 'DeviceManagementConfiguration.Read.All'; Optional = $false; Probe = '/deviceManagement/deviceCompliancePolicies?$top=1&$select=id' },
    @{ Name = 'DeviceManagementApps.Read.All'; Optional = $false; Probe = '/deviceAppManagement/mobileApps?$top=1&$select=id' },
    @{ Name = 'DeviceManagementServiceConfig.Read.All'; Optional = $false; Probe = '/deviceManagement/applePushNotificationCertificate' },
    @{ Name = 'Organization.Read.All'; Optional = $true; Probe = '/organization?$select=id' }
)

# Funções do aplicativo "Azul Nexus – Web" (SPEC §4.4).
$script:WebRoles = @(
    @{ Value = 'Nexus.Admin'; DisplayName = 'Administração'; Description = 'Configura o Azul Nexus, gerencia acessos e vê todos os dados.' },
    @{ Value = 'Nexus.Gestao'; DisplayName = 'Gestão'; Description = 'Consulta indicadores e pendências da sua área.' },
    @{ Value = 'Nexus.Operacao'; DisplayName = 'Operação'; Description = 'Trata pendências e acompanha o estado dos dispositivos.' },
    @{ Value = 'Nexus.Seguranca'; DisplayName = 'Segurança'; Description = 'Acompanha postura, conformidade e exceções.' }
)
$script:WebDelegatedScopes = @('openid', 'profile', 'User.Read')

#region Utilitários puros

# GUID estável a partir de um texto: rodar o provisionamento de novo não duplica as funções do aplicativo.
function New-DeterministicGuid {
    param([Parameter(Mandatory)][string]$Seed)
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try { return [guid]::new($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes("azul-nexus:$Seed"))) } finally { $md5.Dispose() }
}

function ConvertTo-Base64Url {
    param([byte[]]$Bytes)
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function ConvertFrom-Base64Url {
    param([string]$Text)
    $padded = $Text.Replace('-', '+').Replace('_', '/')
    switch ($padded.Length % 4) { 2 { $padded += '==' } 3 { $padded += '=' } }
    return [Convert]::FromBase64String($padded)
}

function ConvertFrom-HexString {
    param([string]$Hex)
    $bytes = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16) }
    return , $bytes
}

# Payload (claims) de um JWT, sem validar assinatura (o token vem direto do endpoint da Microsoft por TLS).
function Get-JwtPayload {
    param([Parameter(Mandatory)][string]$Token)
    $part = $Token.Split('.')[1]
    return [Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $part)) | ConvertFrom-Json
}

# Permissões exigidas que não aparecem na declaração "roles" do token (SPEC §4.7).
function Get-MissingPermissions {
    param([string[]]$GrantedRoles, [switch]$IncludeOptional)
    $granted = @($GrantedRoles)
    return @($script:CollectorPermissions |
        Where-Object { $IncludeOptional -or -not $_.Optional } |
        Where-Object { $granted -inotcontains $_.Name } |
        ForEach-Object { $_.Name })
}

# JWT de asserção do cliente (RS256) assinado com a chave privada do certificado: é assim que o aplicativo
# se autentica sem segredo.
function New-ClientAssertion {
    param([Parameter(Mandatory)]$Certificate, [Parameter(Mandatory)][string]$TenantId, [Parameter(Mandatory)][string]$ClientId)
    $now = [DateTimeOffset]::UtcNow
    $header = @{ alg = 'RS256'; typ = 'JWT'; x5t = (ConvertTo-Base64Url (ConvertFrom-HexString $Certificate.Thumbprint)) } | ConvertTo-Json -Compress
    $payload = @{
        aud = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
        iss = $ClientId; sub = $ClientId; jti = [guid]::NewGuid().ToString()
        nbf = $now.ToUnixTimeSeconds(); iat = $now.ToUnixTimeSeconds(); exp = $now.AddMinutes(10).ToUnixTimeSeconds()
    } | ConvertTo-Json -Compress
    $signingInput = (ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($header))) + '.' + (ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($payload)))
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if (-not $rsa) { throw "O certificado $($Certificate.Thumbprint) não tem chave privada acessível para este usuário." }
    $signature = $rsa.SignData([Text.Encoding]::UTF8.GetBytes($signingInput), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    return $signingInput + '.' + (ConvertTo-Base64Url $signature)
}

function Get-KeyIdentifier {
    param([string]$Thumbprint)
    return [Convert]::ToBase64String((ConvertFrom-HexString $Thumbprint))
}

# Conteúdo de azure.json (sem segredos: só identificadores e impressões digitais). É o contrato com o Nexus.
function New-AzureConfig {
    param($Result, [string]$CollectorCertSubject, [string]$WebCertSubject)
    return [ordered]@{
        schemaVersion = 1
        tenantId      = $Result.TenantId
        tenantName    = $Result.TenantName
        createdAt     = [DateTimeOffset]::UtcNow.ToString('o')
        collector     = [ordered]@{ appName = $script:CollectorAppName; clientId = $Result.Collector.AppId; objectId = $Result.Collector.ObjectId; certificateThumbprint = $Result.Collector.Thumbprint; certificateSubject = $CollectorCertSubject }
        web           = [ordered]@{ appName = $script:WebAppName; clientId = $Result.Web.AppId; objectId = $Result.Web.ObjectId; certificateThumbprint = $Result.Web.Thumbprint; certificateSubject = $WebCertSubject; redirectUri = $Result.Web.RedirectUri; logoutUri = $Result.Web.LogoutUri; roles = @($script:WebRoles | ForEach-Object { $_.Value }) }
        permissions   = @($script:CollectorPermissions | ForEach-Object { $_.Name })
    }
}

#endregion

#region Graph REST

function Get-GraphErrorDetail {
    param($ErrorRecord)
    $text = $ErrorRecord.ErrorDetails.Message
    if (-not $text) { $text = $ErrorRecord.Exception.Message }
    try {
        $json = $text | ConvertFrom-Json
        if ($json.error.message) { return "$($json.error.code): $($json.error.message)" }
        if ($json.error_description) { return "$($json.error): $($json.error_description)" }
    } catch { }
    return $text
}

function Get-HttpStatusCode {
    param($ErrorRecord)
    try { return [int]$ErrorRecord.Exception.Response.StatusCode } catch { return $null }
}

# Chamada ao Graph com novas tentativas para 429/5xx e para 404/400 de propagação logo após criar objetos.
function Invoke-GraphRequest {
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$Token, $Body = $null, [int]$Retries = 6)
    $url = if ($Uri.StartsWith('http')) { $Uri } else { $script:GraphBase + $Uri }
    $headers = @{ Authorization = "Bearer $Token" }
    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        try {
            if ($null -ne $Body) {
                return Invoke-RestMethod -Method $Method -Uri $url -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress))) -UseBasicParsing
            }
            return Invoke-RestMethod -Method $Method -Uri $url -Headers $headers -UseBasicParsing
        } catch {
            $status = Get-HttpStatusCode $_
            $transient = ($status -in @(429, 500, 502, 503, 504)) -or ($Method -ne 'GET' -and $status -in @(400, 404) -and $attempt -lt $Retries -and ((Get-GraphErrorDetail $_) -match 'does not exist|Request_ResourceNotFound|propagat'))
            if ($transient -and $attempt -lt $Retries) { Start-Sleep -Seconds ([math]::Min(30, 3 * $attempt)); continue }
            throw
        }
    }
}

function Get-GraphAll {
    param([Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$Token)
    $items = @()
    $next = $Uri
    while ($next) {
        $page = Invoke-GraphRequest -Method GET -Uri $next -Token $Token
        $items += @($page.value)
        $next = $page.'@odata.nextLink'
    }
    return $items
}

#endregion

#region Autenticação

# Login do administrador por código de dispositivo (device code): abre-se o endereço em qualquer navegador.
# O token fica só em memória.
function Get-AdminToken {
    param([string]$TenantId = 'organizations')
    $device = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/devicecode" -Body @{ client_id = $script:AdminClientId; scope = $script:AdminScopes } -UseBasicParsing
    [Console]::WriteLine('')
    [Console]::WriteLine($device.message)
    [Console]::WriteLine('Entre com a conta de Administrador Global e aprove as permissões solicitadas.')
    $deadline = (Get-Date).AddSeconds([int]$device.expires_in)
    $interval = [math]::Max(3, [int]$device.interval)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $interval
        try {
            $token = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body @{
                grant_type = 'urn:ietf:params:oauth:grant-type:device_code'; client_id = $script:AdminClientId; device_code = $device.device_code } -UseBasicParsing
            return $token.access_token
        } catch {
            $detail = $_.ErrorDetails.Message
            if ($detail -match 'authorization_pending') { continue }
            if ($detail -match 'slow_down') { $interval += 5; continue }
            throw (New-InstallException -ExitCode 20 -Message ("O que aconteceu: o login do administrador falhou ($(Get-GraphErrorDetail $_)).`nImpacto: nada foi criado no Entra ID.`nComo resolver: rode de novo e conclua o login em https://microsoft.com/devicelogin com a conta de Administrador Global; se a empresa bloqueia o aplicativo 'Microsoft Graph Command Line Tools', peça ao time de identidade para liberá-lo."))
        }
    }
    throw (New-InstallException -ExitCode 20 -Message "O que aconteceu: o código de login expirou antes de ser usado.`nImpacto: nada foi criado no Entra ID.`nComo resolver: rode o script de novo e conclua o login em até 15 minutos.")
}

# Token do aplicativo (client credentials com certificado): a identidade que o Nexus usará.
function Get-AppToken {
    param([Parameter(Mandatory)][string]$TenantId, [Parameter(Mandatory)][string]$ClientId, [Parameter(Mandatory)]$Certificate)
    $assertion = New-ClientAssertion -Certificate $Certificate -TenantId $TenantId -ClientId $ClientId
    $response = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -UseBasicParsing -Body @{
        grant_type = 'client_credentials'; client_id = $ClientId; scope = 'https://graph.microsoft.com/.default'
        client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'; client_assertion = $assertion }
    return $response.access_token
}

#endregion

#region Provisionamento

function Find-GraphApplication {
    param([string]$DisplayName, [string]$Token)
    $filter = [uri]::EscapeDataString("displayName eq '$DisplayName'")
    return @(Get-GraphAll -Uri "/applications?`$filter=$filter&`$select=id,appId,displayName,keyCredentials,appRoles,requiredResourceAccess,web" -Token $Token) | Select-Object -First 1
}

function Find-ServicePrincipalByAppId {
    param([string]$AppId, [string]$Token)
    $filter = [uri]::EscapeDataString("appId eq '$AppId'")
    return @(Get-GraphAll -Uri "/servicePrincipals?`$filter=$filter&`$select=id,appId,appRoles,oauth2PermissionScopes,appRoleAssignmentRequired" -Token $Token) | Select-Object -First 1
}

function New-KeyCredential {
    param($Certificate, [string]$Name)
    return @{ type = 'AsymmetricX509Cert'; usage = 'Verify'; displayName = $Name; key = [Convert]::ToBase64String($Certificate.RawData) }
}

# Mantém as chaves já registradas (identificadas pela impressão digital) e acrescenta a do certificado atual.
function Get-MergedKeyCredentials {
    param($Existing, $Certificate, [string]$Name)
    $merged = @()
    $wanted = Get-KeyIdentifier $Certificate.Thumbprint
    $found = $false
    foreach ($key in @($Existing)) {
        if (-not $key) { continue }
        if ($key.customKeyIdentifier -eq $wanted) { $found = $true }
        $merged += @{ type = $key.type; usage = $key.usage; displayName = $key.displayName; keyId = $key.keyId; customKeyIdentifier = $key.customKeyIdentifier; startDateTime = $key.startDateTime; endDateTime = $key.endDateTime }
    }
    if (-not $found) { $merged += (New-KeyCredential -Certificate $Certificate -Name $Name) }
    return , $merged
}

function Confirm-ServicePrincipal {
    param([string]$AppId, [string]$Token)
    $sp = Find-ServicePrincipalByAppId -AppId $AppId -Token $Token
    if ($sp) { return $sp }
    Write-Log "Criando o aplicativo empresarial (service principal) de $AppId."
    $null = Invoke-GraphRequest -Method POST -Uri '/servicePrincipals' -Token $Token -Body @{ appId = $AppId }
    for ($i = 0; $i -lt 10; $i++) {
        $sp = Find-ServicePrincipalByAppId -AppId $AppId -Token $Token
        if ($sp) { return $sp }
        Start-Sleep -Seconds 3
    }
    throw "O service principal de $AppId não apareceu no diretório depois de criado."
}

function Initialize-NexusAzure {
    param(
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$PublicUrl,
        [Parameter(Mandatory)]$CollectorCertificate,
        [Parameter(Mandatory)]$WebCertificate,
        [string]$AdminUpn,
        [switch]$SkipOptionalPermissions
    )
    $result = [ordered]@{ Actions = (New-Object System.Collections.Generic.List[string]) }
    $note = { param($m) $result.Actions.Add($m); Write-Log $m }

    $org = @((Invoke-GraphRequest -Method GET -Uri '/organization?$select=id,displayName' -Token $Token).value)[0]
    $result.TenantId = $org.id; $result.TenantName = $org.displayName
    & $note "Locatário: $($org.displayName) ($($org.id))."

    $graphSp = Find-ServicePrincipalByAppId -AppId $script:GraphAppId -Token $Token
    if (-not $graphSp) { throw 'O aplicativo Microsoft Graph não foi encontrado no locatário.' }

    # ----- Coletor -----
    $needed = @($script:CollectorPermissions | Where-Object { -not ($SkipOptionalPermissions -and $_.Optional) })
    $roleIds = @()
    foreach ($permission in $needed) {
        $role = @($graphSp.appRoles | Where-Object { $_.value -eq $permission.Name -and $_.allowedMemberTypes -contains 'Application' }) | Select-Object -First 1
        if (-not $role) { throw "A permissão de aplicativo $($permission.Name) não existe no Microsoft Graph deste locatário." }
        $roleIds += $role.id
    }
    $required = @(@{ resourceAppId = $script:GraphAppId; resourceAccess = @($roleIds | ForEach-Object { @{ id = $_; type = 'Role' } }) })

    $collector = Find-GraphApplication -DisplayName $script:CollectorAppName -Token $Token
    if (-not $collector) {
        $created = Invoke-GraphRequest -Method POST -Uri '/applications' -Token $Token -Body @{
            displayName = $script:CollectorAppName; signInAudience = 'AzureADMyOrg'; requiredResourceAccess = $required
            keyCredentials = @(New-KeyCredential -Certificate $CollectorCertificate -Name 'nexus-coletor') }
        $collector = $created
        & $note "Registro '$($script:CollectorAppName)' criado."
    } else {
        $keys = Get-MergedKeyCredentials -Existing $collector.keyCredentials -Certificate $CollectorCertificate -Name 'nexus-coletor'
        $null = Invoke-GraphRequest -Method PATCH -Uri "/applications/$($collector.id)" -Token $Token -Body @{ requiredResourceAccess = $required; keyCredentials = $keys }
        & $note "Registro '$($script:CollectorAppName)' já existia: permissões e certificado conferidos."
    }
    $collectorSp = Confirm-ServicePrincipal -AppId $collector.appId -Token $Token

    $assigned = @(Get-GraphAll -Uri "/servicePrincipals/$($collectorSp.id)/appRoleAssignments" -Token $Token)
    $granted = 0
    foreach ($roleId in $roleIds) {
        if (@($assigned | Where-Object { $_.appRoleId -eq $roleId -and $_.resourceId -eq $graphSp.id }).Count -gt 0) { continue }
        $null = Invoke-GraphRequest -Method POST -Uri "/servicePrincipals/$($graphSp.id)/appRoleAssignedTo" -Token $Token -Body @{ principalId = $collectorSp.id; resourceId = $graphSp.id; appRoleId = $roleId }
        $granted++
    }
    & $note "Consentimento do administrador: $granted permissão(ões) concedida(s) agora, $($roleIds.Count - $granted) já concedida(s)."
    $result.Collector = [ordered]@{ AppId = $collector.appId; ObjectId = $collector.id; ServicePrincipalId = $collectorSp.id; Thumbprint = $CollectorCertificate.Thumbprint }

    # ----- Web (SSO) -----
    $base = $PublicUrl.TrimEnd('/')
    $redirect = "$base/signin-oidc"; $logout = "$base/signout-oidc"
    $appRoles = @($script:WebRoles | ForEach-Object {
        @{ id = (New-DeterministicGuid "role:$($_.Value)").ToString(); allowedMemberTypes = @('User'); displayName = $_.DisplayName; description = $_.Description; value = $_.Value; isEnabled = $true } })
    $scopeIds = @()
    foreach ($scopeName in $script:WebDelegatedScopes) {
        $scope = @($graphSp.oauth2PermissionScopes | Where-Object { $_.value -eq $scopeName }) | Select-Object -First 1
        if (-not $scope) { throw "O escopo delegado $scopeName não existe no Microsoft Graph deste locatário." }
        $scopeIds += $scope.id
    }
    $webRequired = @(@{ resourceAppId = $script:GraphAppId; resourceAccess = @($scopeIds | ForEach-Object { @{ id = $_; type = 'Scope' } }) })
    $webSettings = @{ redirectUris = @($redirect); logoutUrl = $logout; implicitGrantSettings = @{ enableIdTokenIssuance = $false; enableAccessTokenIssuance = $false } }

    $web = Find-GraphApplication -DisplayName $script:WebAppName -Token $Token
    if (-not $web) {
        $web = Invoke-GraphRequest -Method POST -Uri '/applications' -Token $Token -Body @{
            displayName = $script:WebAppName; signInAudience = 'AzureADMyOrg'; requiredResourceAccess = $webRequired; appRoles = $appRoles; web = $webSettings
            keyCredentials = @(New-KeyCredential -Certificate $WebCertificate -Name 'nexus-web') }
        & $note "Registro '$($script:WebAppName)' criado com as funções $(($script:WebRoles | ForEach-Object { $_.Value }) -join ', ')."
    } else {
        $keys = Get-MergedKeyCredentials -Existing $web.keyCredentials -Certificate $WebCertificate -Name 'nexus-web'
        $extraRoles = @($web.appRoles | Where-Object { $appRoles.id -notcontains $_.id } | ForEach-Object { @{ id = $_.id; allowedMemberTypes = $_.allowedMemberTypes; displayName = $_.displayName; description = $_.description; value = $_.value; isEnabled = $_.isEnabled } })
        $null = Invoke-GraphRequest -Method PATCH -Uri "/applications/$($web.id)" -Token $Token -Body @{ requiredResourceAccess = $webRequired; appRoles = @($appRoles + $extraRoles); web = $webSettings; keyCredentials = $keys }
        & $note "Registro '$($script:WebAppName)' já existia: funções, URIs e certificado conferidos."
    }
    $webSp = Confirm-ServicePrincipal -AppId $web.appId -Token $Token
    if (-not $webSp.appRoleAssignmentRequired) {
        $null = Invoke-GraphRequest -Method PATCH -Uri "/servicePrincipals/$($webSp.id)" -Token $Token -Body @{ appRoleAssignmentRequired = $true }
        & $note 'Atribuição obrigatória ativada: só quem tem função no Azul Nexus consegue entrar.'
    }

    $existingGrants = @(Get-GraphAll -Uri ("/oauth2PermissionGrants?`$filter=" + [uri]::EscapeDataString("clientId eq '$($webSp.id)'")) -Token $Token)
    $scopeText = $script:WebDelegatedScopes -join ' '
    if (@($existingGrants | Where-Object { $_.resourceId -eq $graphSp.id -and $_.consentType -eq 'AllPrincipals' }).Count -eq 0) {
        $null = Invoke-GraphRequest -Method POST -Uri '/oauth2PermissionGrants' -Token $Token -Body @{ clientId = $webSp.id; consentType = 'AllPrincipals'; resourceId = $graphSp.id; scope = $scopeText }
        & $note "Consentimento do administrador concedido às permissões delegadas ($scopeText)."
    }

    # ----- Primeiro administrador -----
    $adminKey = if ($AdminUpn) { $AdminUpn } else { 'me' }
    $adminUser = if ($AdminUpn) { Invoke-GraphRequest -Method GET -Uri ('/users/' + [uri]::EscapeDataString($AdminUpn) + '?$select=id,userPrincipalName') -Token $Token } else { Invoke-GraphRequest -Method GET -Uri '/me?$select=id,userPrincipalName' -Token $Token }
    $adminRoleId = (New-DeterministicGuid 'role:Nexus.Admin').ToString()
    $userAssignments = @(Get-GraphAll -Uri "/users/$($adminUser.id)/appRoleAssignments" -Token $Token)
    if (@($userAssignments | Where-Object { $_.resourceId -eq $webSp.id -and $_.appRoleId -eq $adminRoleId }).Count -eq 0) {
        $null = Invoke-GraphRequest -Method POST -Uri "/users/$($adminUser.id)/appRoleAssignments" -Token $Token -Body @{ principalId = $adminUser.id; resourceId = $webSp.id; appRoleId = $adminRoleId }
        & $note "$($adminUser.userPrincipalName) atribuído(a) à função Nexus.Admin."
    } else {
        & $note "$($adminUser.userPrincipalName) já era Nexus.Admin."
    }
    $result.Web = [ordered]@{ AppId = $web.appId; ObjectId = $web.id; ServicePrincipalId = $webSp.id; Thumbprint = $WebCertificate.Thumbprint; RedirectUri = $redirect; LogoutUri = $logout; Admin = $adminUser.userPrincipalName }
    return [pscustomobject]$result
}

#endregion

#region Validação (SPEC §4.7)

# Valida com a identidade do aplicativo: token por certificado, declaração "roles" e uma chamada mínima por permissão.
function Test-NexusAzureAccess {
    param([Parameter(Mandatory)][string]$TenantId, [Parameter(Mandatory)][string]$ClientId, [Parameter(Mandatory)]$Certificate, [switch]$IncludeOptional)
    $report = New-Object System.Collections.Generic.List[object]
    try {
        $token = Get-AppToken -TenantId $TenantId -ClientId $ClientId -Certificate $Certificate
    } catch {
        $detail = Get-GraphErrorDetail $_
        $explain = switch -Regex ($detail) {
            'AADSTS700016' { 'Aplicativo não encontrado neste locatário: confira o Client ID e o Tenant ID.' }
            'AADSTS700027' { 'O certificado não foi reconhecido pelo registro: reenvie o .cer e confira a impressão digital.' }
            'AADSTS90002' { 'Locatário não encontrado: confira o Tenant ID.' }
            'AADSTS53003' { 'Bloqueado por Acesso Condicional: peça uma exceção para a identidade do Nexus.' }
            default { $detail }
        }
        $report.Add([pscustomobject]@{ Item = 'Token com o certificado'; Status = 'Falha'; Detail = $explain })
        return $report
    }
    $report.Add([pscustomobject]@{ Item = 'Token com o certificado'; Status = 'OK'; Detail = "Obtido para o aplicativo $ClientId." })

    $roles = @((Get-JwtPayload $token).roles)
    $missing = @(Get-MissingPermissions -GrantedRoles $roles -IncludeOptional:$IncludeOptional)
    if ($missing.Count -eq 0) { $report.Add([pscustomobject]@{ Item = 'Permissões no token (roles)'; Status = 'OK'; Detail = "$($roles.Count) permissão(ões) presentes." }) }
    else { foreach ($name in $missing) { $report.Add([pscustomobject]@{ Item = "Permissão $name"; Status = 'Falha'; Detail = 'Ausente ou sem consentimento do administrador. Rode o script de novo (ele concede o que faltar).' }) } }

    foreach ($permission in $script:CollectorPermissions) {
        if ($permission.Optional -and -not $IncludeOptional) { continue }
        if ($missing -contains $permission.Name) { continue }
        try {
            $null = Invoke-GraphRequest -Method GET -Uri $permission.Probe -Token $token -Retries 1
            $report.Add([pscustomobject]@{ Item = "Chamada: $($permission.Name)"; Status = 'OK'; Detail = $permission.Probe })
        } catch {
            $status = Get-HttpStatusCode $_
            $detail = Get-GraphErrorDetail $_
            if ($status -eq 403 -and $detail -match 'license|licen|Intune|MDM') {
                $report.Add([pscustomobject]@{ Item = "Chamada: $($permission.Name)"; Status = 'Atenção'; Detail = "Sem licença/recurso habilitado no locatário: $detail" })
            } else {
                $report.Add([pscustomobject]@{ Item = "Chamada: $($permission.Name)"; Status = 'Falha'; Detail = "HTTP $status — $detail" })
            }
        }
    }
    return $report
}

#endregion
