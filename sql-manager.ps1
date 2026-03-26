#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('InitConfig', 'AddServer', 'SelectServer', 'SyncServer', 'CreateDatabase', 'CreateUser', 'AddRole', 'ShowUsers', 'RemoveUser', 'UpdatePassword')]
    [string]$Action,

    [string]$ConfigPath = (Join-Path -Path $PSScriptRoot -ChildPath 'sql-config.json'),

    [string]$ServerName,

    [string]$AdminUsername,

    [SecureString]$AdminPassword,

    [string]$DatabaseName,

    [string]$UserName,

    [string[]]$Roles,

    [SecureString]$NewUserPassword,

    [ValidateSet('Database', 'Server', 'Both')]
    [string]$RemovalScope = 'Both'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Ensure-SqlServerModule {
    $loadedModule = Get-Module -Name SqlServer | Sort-Object Version -Descending | Select-Object -First 1
    if ($loadedModule) {
        return
    }

    $installedModule = Get-Module -ListAvailable -Name SqlServer |
        Sort-Object Version -Descending |
        Select-Object -First 1

    if ($installedModule) {
        Import-Module $installedModule.Path -ErrorAction Stop
        return
    }

    $localModuleRoot = Join-Path -Path $PSScriptRoot -ChildPath 'modules\SqlServer'
    if (Test-Path -Path $localModuleRoot) {
        $localManifest = Get-ChildItem -Path $localModuleRoot -Filter 'SqlServer.psd1' -Recurse -File |
            Sort-Object {
                try {
                    [version](Split-Path -Path $_.DirectoryName -Leaf)
                }
                catch {
                    [version]'0.0'
                }
            } -Descending |
            Select-Object -First 1

        if ($localManifest) {
            Import-Module $localManifest.FullName -ErrorAction Stop
            return
        }
    }

    Write-Host 'Installing SqlServer module from PSGallery...'
    Install-Module -Name SqlServer -Scope CurrentUser -Force -AllowClobber

    Import-Module SqlServer -ErrorAction Stop
}

function New-PlainTextPassword {
    param(
        [int]$Length = 20
    )

    if ($Length -lt 15) {
        throw 'Password length must be at least 15 characters.'
    }

    $upper = 'ABCDEFGHJKLMNPQRSTUVWXYZ'
    $lower = 'abcdefghijkmnopqrstuvwxyz'
    $digits = '23456789'
    $special = '!@#$%^&*()-_=+[]{}:?'
    $allChars = ($upper + $lower + $digits + $special).ToCharArray()

    $passwordChars = [System.Collections.Generic.List[char]]::new()
    $passwordChars.Add(($upper.ToCharArray() | Get-Random))
    $passwordChars.Add(($lower.ToCharArray() | Get-Random))
    $passwordChars.Add(($digits.ToCharArray() | Get-Random))
    $passwordChars.Add(($special.ToCharArray() | Get-Random))

    for ($index = $passwordChars.Count; $index -lt $Length; $index++) {
        $passwordChars.Add(($allChars | Get-Random))
    }

    -join ($passwordChars | Sort-Object { Get-Random })
}

function ConvertTo-PlainText {
    param(
        [Parameter(Mandatory = $true)]
        [SecureString]$Value
    )

    $credential = [pscredential]::new('ignored', $Value)
    $credential.GetNetworkCredential().Password
}

function New-ServerConfigObject {
    param(
        [string]$ServerName = '',
        [string]$AdminUsername = ''
    )

    [pscustomobject][ordered]@{
        serverName = $ServerName
        adminUsername = $AdminUsername
        databases = @()
    }
}

function New-DatabaseConfigObject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DatabaseName
    )

    [pscustomobject][ordered]@{
        databaseName = $DatabaseName
        users = @()
    }
}

function New-UserConfigObject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$UserName
    )

    [pscustomobject][ordered]@{
        username = $UserName
        password = ''
        roles = @()
        connectionString = ''
    }
}

function New-DefaultConfig {
    [ordered]@{
        selectedServerName = ''
        servers = @()
    }
}

function ConvertTo-CurrentConfig {
    param(
        [Parameter(Mandatory = $true)]
        [object]$ImportedConfig
    )

    if ($ImportedConfig.PSObject.Properties.Name -contains 'servers') {
        if (-not ($ImportedConfig.PSObject.Properties.Name -contains 'selectedServerName')) {
            $ImportedConfig | Add-Member -NotePropertyName selectedServerName -NotePropertyValue ''
        }

        foreach ($server in @($ImportedConfig.servers)) {
            if (-not ($server.PSObject.Properties.Name -contains 'adminUsername')) {
                $server | Add-Member -NotePropertyName adminUsername -NotePropertyValue ''
            }
            if (-not ($server.PSObject.Properties.Name -contains 'databases')) {
                $server | Add-Member -NotePropertyName databases -NotePropertyValue @()
            }
        }

        return $ImportedConfig
    }

    $config = [pscustomobject](New-DefaultConfig)
    $legacyAdminUsername = ''
    $legacySelectedServer = ''

    if ($ImportedConfig.PSObject.Properties.Name -contains 'sqlAdmin') {
        $legacyAdminUsername = $ImportedConfig.sqlAdmin.username
        $legacySelectedServer = $ImportedConfig.sqlAdmin.serverName
    }

    $legacyDatabases = @()
    if ($ImportedConfig.PSObject.Properties.Name -contains 'databases') {
        $legacyDatabases = @($ImportedConfig.databases)
    }

    $groupedDatabases = $legacyDatabases | Group-Object -Property {
        if ($_.serverName) {
            $_.serverName
        }
        else {
            $legacySelectedServer
        }
    }

    foreach ($group in @($groupedDatabases)) {
        if (-not $group.Name) {
            continue
        }

        $serverConfig = New-ServerConfigObject -ServerName $group.Name -AdminUsername $legacyAdminUsername
        foreach ($database in @($group.Group)) {
            $databaseConfig = New-DatabaseConfigObject -DatabaseName $database.databaseName
            foreach ($user in @($database.users)) {
                $userConfig = New-UserConfigObject -UserName $user.username
                $userConfig.password = $user.password
                $userConfig.roles = @($user.roles)
                $userConfig.connectionString = $user.connectionString
                $databaseConfig.users += $userConfig
            }
            $serverConfig.databases += $databaseConfig
        }
        $config.servers += $serverConfig
    }

    if ($legacySelectedServer -and -not ($config.servers | Where-Object { $_.serverName -eq $legacySelectedServer })) {
        $config.servers += (New-ServerConfigObject -ServerName $legacySelectedServer -AdminUsername $legacyAdminUsername)
    }

    if ($legacySelectedServer) {
        $config.selectedServerName = $legacySelectedServer
    }
    elseif (@($config.servers).Count -eq 1) {
        $config.selectedServerName = $config.servers[0].serverName
    }

    $config
}

function Read-Config {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path)) {
        return [pscustomobject](New-DefaultConfig)
    }

    $importedConfig = Get-Content -Path $Path -Raw | ConvertFrom-Json -Depth 15
    ConvertTo-CurrentConfig -ImportedConfig $importedConfig
}

function Write-Config {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Config
    )

    $parent = Split-Path -Path $Path -Parent
    if ($parent -and -not (Test-Path -Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $Config | ConvertTo-Json -Depth 15 | Set-Content -Path $Path
}

function Get-ServerConfig {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Config,

        [Parameter(Mandatory = $true)]
        [string]$Server
    )

    $Config.servers | Where-Object { $_.serverName -eq $Server } | Select-Object -First 1
}

function Get-OrCreateServerConfig {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Config,

        [Parameter(Mandatory = $true)]
        [string]$Server,

        [string]$AdminUsername
    )

    $entry = Get-ServerConfig -Config $Config -Server $Server
    if ($null -eq $entry) {
        $entry = New-ServerConfigObject -ServerName $Server -AdminUsername $AdminUsername
        $Config.servers += $entry
    }
    elseif ($AdminUsername) {
        $entry.adminUsername = $AdminUsername
    }

    if (-not $Config.selectedServerName) {
        $Config.selectedServerName = $Server
    }

    $entry
}

function Get-OrCreateDatabaseConfig {
    param(
        [Parameter(Mandatory = $true)]
        [object]$ServerConfig,

        [Parameter(Mandatory = $true)]
        [string]$Database
    )

    $entry = $ServerConfig.databases | Where-Object { $_.databaseName -eq $Database } | Select-Object -First 1
    if ($null -eq $entry) {
        $entry = New-DatabaseConfigObject -DatabaseName $Database
        $ServerConfig.databases += $entry
    }

    $entry
}

function Get-OrCreateUserConfig {
    param(
        [Parameter(Mandatory = $true)]
        [object]$DatabaseConfig,

        [Parameter(Mandatory = $true)]
        [string]$User
    )

    $entry = $DatabaseConfig.users | Where-Object { $_.username -eq $User } | Select-Object -First 1
    if ($null -eq $entry) {
        $entry = New-UserConfigObject -UserName $User
        $DatabaseConfig.users += $entry
    }

    $entry
}

function Get-StoredPasswordForUser {
    param(
        [Parameter(Mandatory = $true)]
        [object]$ServerConfig,

        [Parameter(Mandatory = $true)]
        [string]$UserName
    )

    foreach ($database in @($ServerConfig.databases)) {
        $user = $database.users | Where-Object { $_.username -eq $UserName -and $_.password } | Select-Object -First 1
        if ($user) {
            return $user.password
        }
    }

    $null
}

function Remove-UserFromConfigDatabases {
    param(
        [Parameter(Mandatory = $true)]
        [object]$ServerConfig,

        [Parameter(Mandatory = $true)]
        [string]$UserName,

        [string]$DatabaseName
    )

    foreach ($database in @($ServerConfig.databases)) {
        if ($DatabaseName -and $database.databaseName -ne $DatabaseName) {
            continue
        }

        $database.users = @($database.users | Where-Object { $_.username -ne $UserName })
    }
}

function Update-UserPasswordInConfig {
    param(
        [Parameter(Mandatory = $true)]
        [object]$ServerConfig,

        [Parameter(Mandatory = $true)]
        [string]$ServerName,

        [Parameter(Mandatory = $true)]
        [string]$UserName,

        [Parameter(Mandatory = $true)]
        [string]$Password
    )

    foreach ($database in @($ServerConfig.databases)) {
        foreach ($user in @($database.users | Where-Object { $_.username -eq $UserName })) {
            $user.password = $Password
            $user.connectionString = New-ConnectionString -Server $ServerName -Database $database.databaseName -Username $UserName -Password $Password
        }
    }
}

function New-ConnectionString {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [string]$Password
    )

    $resolvedPassword = $Password
    if ([string]::IsNullOrWhiteSpace($resolvedPassword)) {
        $resolvedPassword = '<PASSWORD_REQUIRED>'
    }

    "Server=$Server;Database=$Database;User ID=$Username;Password=$resolvedPassword;Encrypt=True;TrustServerCertificate=True;"
}

function Split-RoleList {
    param(
        [string]$Roles
    )

    if (-not $Roles) {
        return @()
    }

    , @($Roles -split '\s*,\s*' | Where-Object { $_ } | Select-Object -Unique)
}

function Invoke-AdminSql {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password,

        [Parameter(Mandatory = $true)]
        [string]$Query,

        [string]$Database = 'master'
    )

    $plainPassword = ConvertTo-PlainText -Value $Password
    Invoke-Sqlcmd -ServerInstance $Server -Database $Database -Username $Username -Password $plainPassword -Query $Query -EncryptConnection -TrustServerCertificate
}

function Quote-SqlIdentifier {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    '[' + $Name.Replace(']', ']]') + ']'
}

function Quote-SqlLiteral {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    "'" + $Value.Replace("'", "''") + "'"
}

function Normalize-DatabaseRoles {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$InputRoles
    )

    $normalized = foreach ($role in $InputRoles) {
        switch ($role.ToLowerInvariant()) {
            'dbowner' { 'db_owner' }
            'db_owner' { 'db_owner' }
            'dbreader' { 'db_datareader' }
            'db_reader' { 'db_datareader' }
            'db_datareader' { 'db_datareader' }
            'dbwriter' { 'db_datawriter' }
            'db_writer' { 'db_datawriter' }
            'db_datawriter' { 'db_datawriter' }
            default { throw "Unsupported role '$role'. Use dbowner/db_owner, dbreader/db_reader/db_datareader, or dbwriter/db_writer/db_datawriter." }
        }
    }

    @($normalized | Select-Object -Unique)
}

function Set-SelectedServer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Config,

        [Parameter(Mandatory = $true)]
        [string]$ServerName
    )

    $Config.selectedServerName = $ServerName
    Write-Config -Path $Path -Config $Config
}

function Select-ConfiguredServerName {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Config
    )

    $servers = @($Config.servers)
    if ($servers.Count -eq 0) {
        return $null
    }

    if ($Config.selectedServerName) {
        return $Config.selectedServerName
    }

    if ($servers.Count -eq 1) {
        return $servers[0].serverName
    }

    $table = for ($index = 0; $index -lt $servers.Count; $index++) {
        [pscustomobject]@{
            Index = $index + 1
            ServerName = $servers[$index].serverName
            AdminUsername = $servers[$index].adminUsername
        }
    }

    Write-Host 'Multiple servers are configured. Choose a server:'
    Write-Host (($table | Format-Table -AutoSize | Out-String -Width 4096).TrimEnd())

    while ($true) {
        $selection = Read-Host 'Enter the server number'
        if ($selection -as [int]) {
            $selectedIndex = [int]$selection
            if ($selectedIndex -ge 1 -and $selectedIndex -le $servers.Count) {
                return $servers[$selectedIndex - 1].serverName
            }
        }

        Write-Host 'Invalid selection. Try again.'
    }
}

function Resolve-ServerContext {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$Server,

        [string]$Username,

        [switch]$PersistSelection
    )

    $config = Read-Config -Path $Path
    $resolvedServer = $Server
    if (-not $resolvedServer) {
        $resolvedServer = Select-ConfiguredServerName -Config $config
    }

    if (-not $resolvedServer) {
        throw 'No server is configured. Run InitConfig or AddServer first, or pass ServerName explicitly.'
    }

    $serverConfig = Get-ServerConfig -Config $config -Server $resolvedServer
    $resolvedUsername = $Username
    if (-not $resolvedUsername -and $serverConfig) {
        $resolvedUsername = $serverConfig.adminUsername
    }

    if ($PersistSelection) {
        $config.selectedServerName = $resolvedServer
        if ($serverConfig -and $Username) {
            $serverConfig.adminUsername = $Username
        }
        Write-Config -Path $Path -Config $config
    }

    [pscustomobject]@{
        Config = $config
        ServerConfig = $serverConfig
        ServerName = $resolvedServer
        AdminUsername = $resolvedUsername
    }
}

function Resolve-RequestedPassword {
    param(
        [SecureString]$ProvidedPassword,

        [Parameter(Mandatory = $true)]
        [bool]$LoginExists,

        [object]$ServerConfig,

        [Parameter(Mandatory = $true)]
        [string]$UserName
    )

    if ($ProvidedPassword) {
        return [pscustomobject]@{
            Password = ConvertTo-PlainText -Value $ProvidedPassword
            IsGenerated = $false
            WasProvided = $true
        }
    }

    if ($LoginExists -and $ServerConfig) {
        $storedPassword = Get-StoredPasswordForUser -ServerConfig $ServerConfig -UserName $UserName
        if ($storedPassword) {
            return [pscustomobject]@{
                Password = $storedPassword
                IsGenerated = $false
                WasProvided = $false
            }
        }
    }

    if ($LoginExists) {
        throw 'The login already exists, but no password is available in config. Pass NewUserPassword so the script can update the login password and generate a connection string.'
    }

    [pscustomobject]@{
        Password = New-PlainTextPassword -Length 20
        IsGenerated = $true
        WasProvided = $false
    }
}

function Initialize-ConfigFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$Server,

        [string]$Username
    )

    $config = Read-Config -Path $Path
    if ($Server) {
        [void](Get-OrCreateServerConfig -Config $config -Server $Server -AdminUsername $Username)
        $config.selectedServerName = $Server
    }

    Write-Config -Path $Path -Config $config
    Write-Host "Config file is ready at $Path"
}

function Add-ConfigServer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Server,

        [string]$Username
    )

    $config = Read-Config -Path $Path
    [void](Get-OrCreateServerConfig -Config $config -Server $Server -AdminUsername $Username)
    if (-not $config.selectedServerName) {
        $config.selectedServerName = $Server
    }

    Write-Config -Path $Path -Config $config
    Write-Host "Server '$Server' has been added to the config."
}

function Select-ConfigServer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Server
    )

    $config = Read-Config -Path $Path
    $serverConfig = Get-ServerConfig -Config $config -Server $Server
    if (-not $serverConfig) {
        throw "Server '$Server' was not found in the config."
    }

    Set-SelectedServer -Path $Path -Config $config -ServerName $Server
    Write-Host "Selected server '$Server'."
}

function Create-Database {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password,

        [Parameter(Mandatory = $true)]
        [string]$Database
    )

    $config = Read-Config -Path $Path
    $quotedDatabase = Quote-SqlIdentifier -Name $Database
    $databaseLiteral = Quote-SqlLiteral -Value $Database
    $query = @"
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = $databaseLiteral)
BEGIN
    EXEC('CREATE DATABASE $quotedDatabase');
END
"@

    Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $query

    $serverConfig = Get-OrCreateServerConfig -Config $config -Server $Server -AdminUsername $Username
    [void](Get-OrCreateDatabaseConfig -ServerConfig $serverConfig -Database $Database)
    $config.selectedServerName = $Server
    Write-Config -Path $Path -Config $config

    Write-Host "Database '$Database' is ready on '$Server'."
}

function Create-DatabaseUser {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password,

        [Parameter(Mandatory = $true)]
        [string]$Database,

        [Parameter(Mandatory = $true)]
        [string]$NewUser,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string[]]$DatabaseRoles,

        [SecureString]$RequestedPassword
    )

    $config = Read-Config -Path $Path
    $serverConfig = Get-OrCreateServerConfig -Config $config -Server $Server -AdminUsername $Username
    $normalizedRoles = Normalize-DatabaseRoles -InputRoles $DatabaseRoles
    $quotedDatabase = Quote-SqlIdentifier -Name $Database
    $quotedUser = Quote-SqlIdentifier -Name $NewUser
    $userLiteral = Quote-SqlLiteral -Value $NewUser

    $loginCheckQuery = "SELECT COUNT(1) AS LoginCount FROM sys.server_principals WHERE name = $userLiteral;"
    $loginCheckResult = Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $loginCheckQuery
    $loginExists = [int]$loginCheckResult.LoginCount -gt 0
    $resolvedPassword = Resolve-RequestedPassword -ProvidedPassword $RequestedPassword -LoginExists $loginExists -ServerConfig $serverConfig -UserName $NewUser

    if (-not $loginExists) {
        $createLoginStatement = "CREATE LOGIN $quotedUser WITH PASSWORD = $(Quote-SqlLiteral -Value $resolvedPassword.Password), CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = $quotedDatabase"
        $createLoginStatementLiteral = Quote-SqlLiteral -Value $createLoginStatement
        $createLoginQuery = @"
EXEC($createLoginStatementLiteral);
ALTER LOGIN $quotedUser ENABLE;
"@
        Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $createLoginQuery
    }
    elseif ($RequestedPassword) {
        $alterLoginStatement = "ALTER LOGIN $quotedUser WITH PASSWORD = $(Quote-SqlLiteral -Value $resolvedPassword.Password)"
        $alterLoginStatementLiteral = Quote-SqlLiteral -Value $alterLoginStatement
        Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query "EXEC($alterLoginStatementLiteral); ALTER LOGIN $quotedUser ENABLE;"
    }

    $databaseQueryBuilder = [System.Text.StringBuilder]::new()
    [void]$databaseQueryBuilder.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = $userLiteral)")
    [void]$databaseQueryBuilder.AppendLine('BEGIN')
    [void]$databaseQueryBuilder.AppendLine("    CREATE USER $quotedUser FOR LOGIN $quotedUser;")
    [void]$databaseQueryBuilder.AppendLine('END')
    foreach ($role in $normalizedRoles) {
        $quotedRole = Quote-SqlIdentifier -Name $role
        [void]$databaseQueryBuilder.AppendLine("ALTER ROLE $quotedRole ADD MEMBER $quotedUser;")
    }

    Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $databaseQueryBuilder.ToString() -Database $Database

    $databaseConfig = Get-OrCreateDatabaseConfig -ServerConfig $serverConfig -Database $Database
    $userConfig = Get-OrCreateUserConfig -DatabaseConfig $databaseConfig -User $NewUser
    $userConfig.password = $resolvedPassword.Password
    $userConfig.roles = @($normalizedRoles)
    $userConfig.connectionString = New-ConnectionString -Server $Server -Database $Database -Username $NewUser -Password $resolvedPassword.Password
    $config.selectedServerName = $Server
    Write-Config -Path $Path -Config $config

    Write-Host "SQL user '$NewUser' is ready for database '$Database'."
    if ($resolvedPassword.IsGenerated) {
        Write-Host 'Password was generated automatically.'
    }
    elseif ($RequestedPassword) {
        Write-Host 'Password was set from the supplied NewUserPassword value.'
    }
    else {
        Write-Host 'Password was reused from the config.'
    }
    Write-Host "Connection string: $($userConfig.connectionString)"
}

function Add-DatabaseRole {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password,

        [Parameter(Mandatory = $true)]
        [string]$Database,

        [Parameter(Mandatory = $true)]
        [string]$ExistingUser,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string[]]$DatabaseRoles
    )

    $config = Read-Config -Path $Path
    $serverConfig = Get-OrCreateServerConfig -Config $config -Server $Server -AdminUsername $Username
    $normalizedRoles = Normalize-DatabaseRoles -InputRoles $DatabaseRoles
    $quotedUser = Quote-SqlIdentifier -Name $ExistingUser
    $userLiteral = Quote-SqlLiteral -Value $ExistingUser
    $queryBuilder = [System.Text.StringBuilder]::new()
    [void]$queryBuilder.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = $userLiteral)")
    [void]$queryBuilder.AppendLine("    THROW 50000, 'Database user does not exist.', 1;")
    foreach ($role in $normalizedRoles) {
        $quotedRole = Quote-SqlIdentifier -Name $role
        [void]$queryBuilder.AppendLine("ALTER ROLE $quotedRole ADD MEMBER $quotedUser;")
    }

    Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $queryBuilder.ToString() -Database $Database

    $databaseConfig = Get-OrCreateDatabaseConfig -ServerConfig $serverConfig -Database $Database
    $userConfig = Get-OrCreateUserConfig -DatabaseConfig $databaseConfig -User $ExistingUser
    $userConfig.roles = @(($userConfig.roles + $normalizedRoles) | Select-Object -Unique)
    $userConfig.connectionString = New-ConnectionString -Server $Server -Database $Database -Username $ExistingUser -Password $userConfig.password
    $config.selectedServerName = $Server
    Write-Config -Path $Path -Config $config

    Write-Host "Added role(s) '$($normalizedRoles -join ', ')' to '$ExistingUser' on '$Database'."
}

function Get-DatabaseUserRows {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password,

        [Parameter(Mandatory = $true)]
        [string]$Database
    )

    $query = @"
SELECT
    dp.name AS UserName,
    ISNULL(SUSER_SNAME(dp.sid), '') AS LoginName,
    ISNULL(
        STUFF((
            SELECT ', ' + rp.name
            FROM sys.database_role_members drm
            INNER JOIN sys.database_principals rp ON drm.role_principal_id = rp.principal_id
            WHERE drm.member_principal_id = dp.principal_id
            ORDER BY rp.name
            FOR XML PATH(''), TYPE
        ).value('.', 'nvarchar(max)'), 1, 2, ''),
        ''
    ) AS Roles
FROM sys.database_principals dp
WHERE dp.type IN ('S', 'U', 'G')
  AND dp.principal_id > 4
  AND dp.name NOT IN ('dbo', 'guest', 'INFORMATION_SCHEMA', 'sys')
ORDER BY dp.name;
"@

    @(
        Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $query -Database $Database
    )
}

function Show-DatabaseUsers {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password,

        [Parameter(Mandatory = $true)]
        [string]$Database
    )

    $rows = Get-DatabaseUserRows -Server $Server -Username $Username -Password $Password -Database $Database

    if ($rows.Count -eq 0) {
        Write-Host "No matching users were found in database '$Database'."
        return
    }

    Write-Host (($rows | Format-Table -AutoSize | Out-String -Width 4096).TrimEnd())
}

function Remove-DatabaseUser {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password,

        [Parameter(Mandatory = $true)]
        [string]$TargetUser,

        [string]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Scope
    )

    $config = Read-Config -Path $Path
    $serverConfig = Get-OrCreateServerConfig -Config $config -Server $Server -AdminUsername $Username
    $quotedUser = Quote-SqlIdentifier -Name $TargetUser
    $userLiteral = Quote-SqlLiteral -Value $TargetUser

    if ($Scope -eq 'Database') {
        if (-not $Database) {
            throw 'DatabaseName is required when RemovalScope is Database.'
        }

        $dropUserQuery = "IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = $userLiteral) DROP USER $quotedUser;"
        Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $dropUserQuery -Database $Database
        Remove-UserFromConfigDatabases -ServerConfig $serverConfig -UserName $TargetUser -DatabaseName $Database
    }
    else {
        $databaseRows = @(
            Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name;"
        )

        foreach ($databaseRow in $databaseRows) {
            $databaseName = $databaseRow.name
            $dropUserQuery = "IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = $userLiteral) DROP USER $quotedUser;"
            Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $dropUserQuery -Database $databaseName
        }

        $dropLoginQuery = "IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = $userLiteral) DROP LOGIN $quotedUser;"
        Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $dropLoginQuery
        Remove-UserFromConfigDatabases -ServerConfig $serverConfig -UserName $TargetUser
    }

    $config.selectedServerName = $Server
    Write-Config -Path $Path -Config $config
    Write-Host "Removed '$TargetUser' using scope '$Scope'."
}

function Update-LoginPassword {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password,

        [Parameter(Mandatory = $true)]
        [string]$TargetUser,

        [SecureString]$RequestedPassword
    )

    $config = Read-Config -Path $Path
    $serverConfig = Get-OrCreateServerConfig -Config $config -Server $Server -AdminUsername $Username
    $newPassword = if ($RequestedPassword) { ConvertTo-PlainText -Value $RequestedPassword } else { New-PlainTextPassword -Length 20 }
    $quotedUser = Quote-SqlIdentifier -Name $TargetUser
    $userLiteral = Quote-SqlLiteral -Value $TargetUser
    $loginCheckQuery = "SELECT COUNT(1) AS LoginCount FROM sys.server_principals WHERE name = $userLiteral;"
    $loginCheckResult = Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query $loginCheckQuery
    if ([int]$loginCheckResult.LoginCount -eq 0) {
        throw "Login '$TargetUser' does not exist on '$Server'."
    }

    $alterLoginStatement = "ALTER LOGIN $quotedUser WITH PASSWORD = $(Quote-SqlLiteral -Value $newPassword)"
    $alterLoginStatementLiteral = Quote-SqlLiteral -Value $alterLoginStatement
    Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query "EXEC($alterLoginStatementLiteral);"

    Update-UserPasswordInConfig -ServerConfig $serverConfig -ServerName $Server -UserName $TargetUser -Password $newPassword
    $config.selectedServerName = $Server
    Write-Config -Path $Path -Config $config

    Write-Host "Password updated for '$TargetUser'."
}

function Sync-ServerToConfig {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Server,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [SecureString]$Password
    )

    $config = Read-Config -Path $Path
    $serverConfig = Get-OrCreateServerConfig -Config $config -Server $Server -AdminUsername $Username
    $databaseRows = @(
        Invoke-AdminSql -Server $Server -Username $Username -Password $Password -Query "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name;"
    )

    $synchronizedDatabases = [System.Collections.Generic.List[object]]::new()
    $userCount = 0

    foreach ($databaseRow in $databaseRows) {
        $databaseName = $databaseRow.name
        $existingDatabaseConfig = $serverConfig.databases | Where-Object { $_.databaseName -eq $databaseName } | Select-Object -First 1
        $databaseConfig = New-DatabaseConfigObject -DatabaseName $databaseName
        $userRows = Get-DatabaseUserRows -Server $Server -Username $Username -Password $Password -Database $databaseName

        foreach ($userRow in $userRows) {
            $userConfig = New-UserConfigObject -UserName $userRow.UserName
            $existingUserConfig = $null
            if ($existingDatabaseConfig) {
                $existingUserConfig = $existingDatabaseConfig.users | Where-Object { $_.username -eq $userRow.UserName } | Select-Object -First 1
            }

            if ($existingUserConfig) {
                $userConfig.password = $existingUserConfig.password
            }

            $userConfig.roles = @(Split-RoleList -Roles $userRow.Roles)
            $userConfig.connectionString = New-ConnectionString -Server $Server -Database $databaseName -Username $userRow.UserName -Password $userConfig.password

            $databaseConfig.users += $userConfig
            $userCount++
        }

        $synchronizedDatabases.Add($databaseConfig)
    }

    $serverConfig.databases = @($synchronizedDatabases)
    $serverConfig.adminUsername = $Username
    $config.selectedServerName = $Server
    Write-Config -Path $Path -Config $config

    Write-Host "Synchronized $($databaseRows.Count) database(s) and $userCount user record(s) from '$Server'."
}

Ensure-SqlServerModule

switch ($Action) {
    'InitConfig' {
        Initialize-ConfigFile -Path $ConfigPath -Server $ServerName -Username $AdminUsername
    }
    'AddServer' {
        if (-not $ServerName) {
            throw 'AddServer requires ServerName.'
        }

        Add-ConfigServer -Path $ConfigPath -Server $ServerName -Username $AdminUsername
    }
    'SelectServer' {
        if (-not $ServerName) {
            throw 'SelectServer requires ServerName.'
        }

        Select-ConfigServer -Path $ConfigPath -Server $ServerName
    }
    'SyncServer' {
        $serverContext = Resolve-ServerContext -Path $ConfigPath -Server $ServerName -Username $AdminUsername -PersistSelection
        if (-not $serverContext.ServerName -or -not $serverContext.AdminUsername -or -not $AdminPassword) {
            throw 'SyncServer requires AdminPassword, plus ServerName/AdminUsername either as parameters or already stored in the config.'
        }

        Sync-ServerToConfig -Path $ConfigPath -Server $serverContext.ServerName -Username $serverContext.AdminUsername -Password $AdminPassword
    }
    'CreateDatabase' {
        $serverContext = Resolve-ServerContext -Path $ConfigPath -Server $ServerName -Username $AdminUsername -PersistSelection
        if (-not $serverContext.ServerName -or -not $serverContext.AdminUsername -or -not $AdminPassword -or -not $DatabaseName) {
            throw 'CreateDatabase requires AdminPassword and DatabaseName, plus ServerName/AdminUsername either as parameters or already stored in the config.'
        }

        Create-Database -Path $ConfigPath -Server $serverContext.ServerName -Username $serverContext.AdminUsername -Password $AdminPassword -Database $DatabaseName
    }
    'CreateUser' {
        $serverContext = Resolve-ServerContext -Path $ConfigPath -Server $ServerName -Username $AdminUsername -PersistSelection
        if (-not $serverContext.ServerName -or -not $serverContext.AdminUsername -or -not $AdminPassword -or -not $DatabaseName -or -not $UserName -or -not $Roles) {
            throw 'CreateUser requires AdminPassword, DatabaseName, UserName, and Roles, plus ServerName/AdminUsername either as parameters or already stored in the config.'
        }

        Create-DatabaseUser -Path $ConfigPath -Server $serverContext.ServerName -Username $serverContext.AdminUsername -Password $AdminPassword -Database $DatabaseName -NewUser $UserName -DatabaseRoles $Roles -RequestedPassword $NewUserPassword
    }
    'AddRole' {
        $serverContext = Resolve-ServerContext -Path $ConfigPath -Server $ServerName -Username $AdminUsername -PersistSelection
        if (-not $serverContext.ServerName -or -not $serverContext.AdminUsername -or -not $AdminPassword -or -not $DatabaseName -or -not $UserName -or -not $Roles) {
            throw 'AddRole requires AdminPassword, DatabaseName, UserName, and Roles, plus ServerName/AdminUsername either as parameters or already stored in the config.'
        }

        Add-DatabaseRole -Path $ConfigPath -Server $serverContext.ServerName -Username $serverContext.AdminUsername -Password $AdminPassword -Database $DatabaseName -ExistingUser $UserName -DatabaseRoles $Roles
    }
    'ShowUsers' {
        $serverContext = Resolve-ServerContext -Path $ConfigPath -Server $ServerName -Username $AdminUsername -PersistSelection
        if (-not $serverContext.ServerName -or -not $serverContext.AdminUsername -or -not $AdminPassword -or -not $DatabaseName) {
            throw 'ShowUsers requires AdminPassword and DatabaseName, plus ServerName/AdminUsername either as parameters or already stored in the config.'
        }

        Show-DatabaseUsers -Server $serverContext.ServerName -Username $serverContext.AdminUsername -Password $AdminPassword -Database $DatabaseName
    }
    'RemoveUser' {
        $serverContext = Resolve-ServerContext -Path $ConfigPath -Server $ServerName -Username $AdminUsername -PersistSelection
        if (-not $serverContext.ServerName -or -not $serverContext.AdminUsername -or -not $AdminPassword -or -not $UserName) {
            throw 'RemoveUser requires AdminPassword and UserName, plus ServerName/AdminUsername either as parameters or already stored in the config.'
        }

        Remove-DatabaseUser -Path $ConfigPath -Server $serverContext.ServerName -Username $serverContext.AdminUsername -Password $AdminPassword -TargetUser $UserName -Database $DatabaseName -Scope $RemovalScope
    }
    'UpdatePassword' {
        $serverContext = Resolve-ServerContext -Path $ConfigPath -Server $ServerName -Username $AdminUsername -PersistSelection
        if (-not $serverContext.ServerName -or -not $serverContext.AdminUsername -or -not $AdminPassword -or -not $UserName) {
            throw 'UpdatePassword requires AdminPassword and UserName, plus ServerName/AdminUsername either as parameters or already stored in the config.'
        }

        Update-LoginPassword -Path $ConfigPath -Server $serverContext.ServerName -Username $serverContext.AdminUsername -Password $AdminPassword -TargetUser $UserName -RequestedPassword $NewUserPassword
    }
}