# SQL Manager

SQL Server and PostgreSQL management utility with:

- the original PowerShell automation script
- a .NET 10 command-line utility
- a built-in terminal UI for interactive management

## Files

- `sql-manager.ps1`: Main automation script.
- `sql-config.json`: Stores multiple servers, provider settings, selected server, databases, users, roles, and connection strings.
- `src/SqlManager`: .NET 10 implementation that can be published as single-file binaries for Windows, macOS, and Linux.

## Requirements

- PowerShell 7 only
- Network access to the target SQL Server or PostgreSQL host
- Permission to install PowerShell modules for the current user

For the .NET utility:

- .NET SDK 10 to build locally
- No .NET installation is required on the target machine when using the self-contained publish output
- Choose the runtime identifier that matches the target OS and architecture, for example `win-x64`, `osx-arm64`, `linux-x64`, or `linux-musl-x64`

The PowerShell script checks for the module in the local `modules` directory first, then falls back to installed modules, and only then installs it:

```powershell
Install-Module -Name SqlServer
```

## Config Structure

```json
{
  "selectedServerName": "pg01.contoso.local",
  "timeouts": {
    "connectionTimeoutSeconds": 15,
    "commandTimeoutSeconds": 30
  },
  "servers": [
    {
      "serverName": "sql01.contoso.local",
      "provider": "sqlserver",
      "adminDatabase": "master",
      "adminUsername": "sa",
      "adminPassword": "ServerAdminSecret123!",
      "databases": [
        {
          "databaseName": "LabDB",
          "users": [
            {
              "username": "LabDBUser",
              "password": "stored-password",
              "roles": ["db_owner", "db_datareader"],
              "connectionString": "Server=sql01.contoso.local;Database=LabDB;User ID=LabDBUser;Password=stored-password;Encrypt=True;TrustServerCertificate=True;"
            }
          ]
        }
      ]
    },
    {
      "serverName": "pg01.contoso.local",
      "provider": "postgresql",
      "port": 5432,
      "adminDatabase": "postgres",
      "adminUsername": "postgres",
      "adminPassword": "PostgresAdminSecret123!",
      "databases": [
        {
          "databaseName": "appdb",
          "users": [
            {
              "username": "app_reader",
              "password": "stored-password",
              "roles": ["db_datareader"],
              "connectionString": "Host=pg01.contoso.local;Port=5432;Database=appdb;Username=app_reader;Password=stored-password;Ssl Mode=Require;"
            }
          ]
        }
      ]
    }
  ]
}
```

Provider-aware config notes:

- `provider` supports `sqlserver` and `postgresql`.
- `port` is optional. If omitted, the server provider default is used.
- `adminDatabase` defaults to `master` for SQL Server and `postgres` for PostgreSQL.
- The CLI keeps generic role names in config: `db_owner`, `db_datareader`, and `db_datawriter`.
- SQL syntax is generated per provider at runtime. SQL Server uses native database roles. PostgreSQL uses provider-specific role and grant statements behind the same generic config roles.

## Examples

### .NET CLI and TUI

Run the interactive terminal UI:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj
```

The TUI is implemented with `Terminal.Gui`. It keeps an active server selection, server-scoped actions use that selected server, and each dialog exposes button-based `Run`, `Back`, and `Cancel` actions.

The main TUI home screen is organized into three columns:

- `Server Management`
- `User Management`
- `Configuration`

Show help:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- help
```

Create a database with the .NET CLI:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- create-database --server-name sql01.contoso.local --admin-username sa --admin-password "Secret123!" --database-name LabDB
```

Show live databases on the selected server:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- show-databases --server-name sql01.contoso.local --admin-username sa --admin-password "Secret123!"
```

Remove a database:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- remove-database --server-name sql01.contoso.local --admin-username sa --admin-password "Secret123!" --database-name LabDB
```

Create a user with generated password and roles:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- create-user --server-name sql01.contoso.local --admin-username sa --admin-password "Secret123!" --database-name LabDB --user-name LabDBUser --roles db_owner,db_datareader
```

Add a PostgreSQL server to the config:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- add-server --server-name pg01.contoso.local --provider postgresql --port 5432 --admin-database postgres --admin-username postgres --admin-password "Secret123!"
```

Create a PostgreSQL database:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- create-database --server-name pg01.contoso.local --admin-username postgres --admin-password "Secret123!" --database-name appdb
```

Create a PostgreSQL user with the same generic role syntax:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- create-user --server-name pg01.contoso.local --admin-username postgres --admin-password "Secret123!" --database-name appdb --user-name app_reader --roles db_datareader
```

PowerShell-style compatibility is also supported:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- --action CreateUser --server-name sql01.contoso.local --admin-username sa --admin-password "Secret123!" --database-name LabDB --user-name LabDBUser --roles dbowner
```

The .NET app is provider-aware. The legacy PowerShell script remains SQL Server-oriented.

Publish a single-file self-contained binary for one runtime:

```powershell
dotnet publish .\src\SqlManager\SqlManager.csproj -c Release -r win-x64 --self-contained true
```

Examples for other targets:

```powershell
dotnet publish .\src\SqlManager\SqlManager.csproj -c Release -r osx-arm64 --self-contained true
dotnet publish .\src\SqlManager\SqlManager.csproj -c Release -r linux-x64 --self-contained true
```

Use the build script to publish one or more production runtimes into separate artifact folders:

```powershell
.\build.ps1 -Target Prod -Runtime win-x64,osx-arm64,linux-x64
```

Use `All` to publish the full runtime matrix currently supported by the script:

```powershell
.\build.ps1 -Target Prod -Runtime All
```

Use a specific CI or release build number without updating `build-number.txt`:

```powershell
.\build.ps1 -Target Prod -Runtime win-x64,linux-x64 -BuildNumber 42
```

Use an explicit full version, for example to match a release tag exactly:

```powershell
.\build.ps1 -Target Prod -Runtime win-x64,linux-x64 -VersionOverride 1.0.0
```

Published outputs are written to runtime-specific folders under:

```text
artifacts\prod\<runtime>
```

### GitHub Releases

The repository can publish release binaries automatically from a Git tag using the GitHub Actions workflow in `.github/workflows/release.yml`.

The workflow:

- restores and validates the solution with `test.ps1`
- publishes self-contained single-file binaries for the supported runtime matrix
- packages each runtime output as a `.zip` asset
- creates or updates a GitHub release for the tag and uploads the packaged binaries

The workflow uses the tag name directly as the release version. Tags must be numeric versions like:

```text
1.0.0
1.0.0.1
```

If a tag is not a numeric version, the release workflow fails.

Run validation locally with:

```powershell
.\test.ps1 -Configuration Release
```

### PowerShell Script

Initialize the config file:

```powershell
.\sql-manager.ps1 -Action InitConfig -ConfigPath .\sql-config.json
```

Add two servers to the config:

```powershell
.\sql-manager.ps1 -Action AddServer -ConfigPath .\sql-config.json -ServerName sql01.contoso.local -AdminUsername sa
.\sql-manager.ps1 -Action AddServer -ConfigPath .\sql-config.json -ServerName sql02.contoso.local -AdminUsername admin_user
```

Select a default server from the config:

```powershell
.\sql-manager.ps1 -Action SelectServer -ConfigPath .\sql-config.json -ServerName sql01.contoso.local
```

Synchronize the config with databases and users already present on a server:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
.\sql-manager.ps1 -Action SyncServer -AdminPassword $adminPassword -ServerName sql01.contoso.local -AdminUsername sa
```

Create a database:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
.\sql-manager.ps1 -Action CreateDatabase -AdminPassword $adminPassword -DatabaseName LabDB
```

Create a user with generated password and roles:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
.\sql-manager.ps1 -Action CreateUser -AdminPassword $adminPassword -DatabaseName LabDB -UserName LabDBUser -Roles dbowner,db_reader,db_writer
```

Create a user with a specific password:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
$newUserPassword = Read-Host "New SQL user password" -AsSecureString
.\sql-manager.ps1 -Action CreateUser -AdminPassword $adminPassword -DatabaseName LabDB -UserName LabDBUser -Roles dbowner -NewUserPassword $newUserPassword
```

Add a role to an existing user:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
.\sql-manager.ps1 -Action AddRole -AdminPassword $adminPassword -DatabaseName LabDB -UserName LabDBUser -Roles db_reader
```

Show existing users and roles in a table:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
.\sql-manager.ps1 -Action ShowUsers -AdminPassword $adminPassword -DatabaseName LabDB
```

Remove a user from only one database:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
.\sql-manager.ps1 -Action RemoveUser -AdminPassword $adminPassword -DatabaseName LabDB -UserName LabDBUser -RemovalScope Database
```

Remove a user and login from the whole server:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
.\sql-manager.ps1 -Action RemoveUser -AdminPassword $adminPassword -UserName LabDBUser -RemovalScope Server
```

Update a user's password:

```powershell
$adminPassword = Read-Host "SQL admin password" -AsSecureString
$updatedPassword = Read-Host "Updated SQL user password" -AsSecureString
.\sql-manager.ps1 -Action UpdatePassword -AdminPassword $adminPassword -UserName LabDBUser -NewUserPassword $updatedPassword
```

## Notes

- Multiple servers are supported in `sql-config.json`. When `ServerName` is omitted, the script uses the selected server or prompts you to choose one from the config.
- The .NET utility defaults to TUI mode when started without arguments and falls back to subcommands when arguments are supplied.
- The TUI exposes an explicit main-menu exit action and a `Select active server` flow that lists configured servers together with their admin usernames and whether an admin password is already saved.
- `View Config` is the place to inspect the config summary, configured servers, tracked databases, and tracked users.
- The root `timeouts` section in `sql-config.json` controls SQL connection timeout and SQL command timeout for the .NET utility.
- The .NET utility wraps command execution, SQL access, JSON loading, and TUI actions in guarded error handling so user-facing failures return clean messages instead of unhandled crashes.
- `SyncServer` replaces the selected server's database list in the config with the current live state from SQL Server and preserves any passwords already stored for matching users.
- If `SyncServer` does not know a user's password, it still generates a connection string template with `Password=<PASSWORD_REQUIRED>` so the connection details are complete apart from the secret.
- `AddServer` can store the server admin password in `sql-config.json`, and later CLI or TUI operations reuse the saved admin username and password when those options are omitted.
- `ShowDatabases`, `CreateDatabase`, `RemoveDatabase`, `CreateUser`, `AddRole`, `ShowUsers`, `RemoveUser`, and `UpdatePassword` can read `ServerName`, `AdminUsername`, and the saved admin password from `sql-config.json`.
- Accepted role names are `dbowner` or `db_owner`, `dbreader` or `db_reader` or `db_datareader`, and `dbwriter` or `db_writer` or `db_datawriter`.
- Generated passwords are 20 characters long and include uppercase, lowercase, numeric, and special characters.
- If a login already exists and the script does not know its password, pass `-NewUserPassword` so the script can update the login and create a correct connection string.
- The config stores server admin passwords and user passwords in plain text because it also stores ready-to-use connection strings. Treat `sql-config.json` as sensitive.
