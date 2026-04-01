# SQL Manager

`sql-manager` is a terminal-first SQL Server, PostgreSQL, and MySQL/MariaDB management tool built on .NET 10. It can run as a command-line utility or as a full-screen `Terminal.Gui` application.

## Highlights

- Manage multiple SQL Server, PostgreSQL, and MySQL/MariaDB connections from one `sql-config.json`
- Create and remove databases, create users, update passwords, show users and databases, and manage roles
- Use the CLI for scripting or the TUI for interactive day-to-day administration
- Configure provider-aware connection settings per server: port, admin database, SQL Server trust mode, PostgreSQL SSL mode, MySQL SSL mode, pooling, MySQL public key retrieval, and per-server timeouts
- Protect the config at rest with Argon2id-derived encryption and AES-256-GCM, with migration support for older encrypted config formats
- Track selected connection, theme preference, version history, and deleted config entries in a built-in trash bin
- Build and publish self-contained single-file binaries for Windows, macOS, Linux, and musl targets

## Security

Treat `sql-config.json` as sensitive data.

When config encryption is disabled, stored admin and user passwords may be written in plaintext. When config encryption is enabled, the full config payload is encrypted at rest and stored connection strings remain masked.

## Repository Layout

- `src/SqlManager`: current .NET 10 application
- `tests/SqlManager.Tests`: xUnit test project
- `build.ps1`: local build and publish helper
- `test.ps1`: restore and test helper
- `sql-manager.ps1`: older PowerShell implementation retained in the repo; the primary app is the .NET project under `src/SqlManager`

## Requirements

- .NET 10 SDK to build or run from source
- PowerShell 7 to use `build.ps1` and `test.ps1`
- Network access to the target SQL Server, PostgreSQL, MySQL, or MariaDB host
- No .NET installation on the target machine when using self-contained publish output

## Quick Start

Run the TUI:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj --
```

Show help:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- help
```

Show version information:

```powershell
dotnet run --project .\src\SqlManager\SqlManager.csproj -- version
```

After publishing, the Windows binary is `sql-manager.exe`. On Linux and macOS self-contained publishes, the binary is `sql-manager`.

## Config File

The default config path is `sql-config.json` next to the executable.

The app stores more metadata than older versions did. A shortened config example looks like this, and a fuller three-provider sample is available at `docs/sample-sql-config.json`:

```json
{
  "selectedServerName": "1",
  "themeName": "iTerm2 Tango Dark",
  "encryptPasswords": true,
  "encryptionKey": "<unlock-password-verifier>",
  "timeouts": {
    "connectionTimeoutSeconds": 15,
    "commandTimeoutSeconds": 30
  },
  "servers": [
    {
      "serverIdentifier": "1",
      "displayName": "Primary SQL Server",
      "serverName": "sql01.contoso.local",
      "provider": "sqlserver",
      "port": 1433,
      "adminDatabase": "master",
      "adminUsername": "sa",
      "adminPassword": "<encrypted-or-plain>",
      "sqlServerTrustMode": "false",
      "connectionTimeoutSeconds": 15,
      "commandTimeoutSeconds": 30,
      "encrypted": true,
      "databases": [
        {
          "databaseName": "LabDB",
          "users": [
            {
              "username": "LabDBUser",
              "password": "<encrypted-or-plain>",
              "encrypted": true,
              "roles": [
                "db_owner",
                "db_datareader"
              ],
              "connectionString": "Server=tcp:sql01.contoso.local,1433;Initial Catalog=LabDB;User ID=LabDBUser;Password=********;Encrypt=False;TrustServerCertificate=False;"
            }
          ]
        }
      ]
    },
    {
      "serverIdentifier": "2",
      "displayName": "Primary PostgreSQL",
      "serverName": "pg01.contoso.local",
      "provider": "postgresql",
      "port": 5432,
      "adminDatabase": "postgres",
      "adminUsername": "postgres",
      "adminPassword": "<encrypted-or-plain>",
      "postgreSqlSslMode": "require",
      "postgreSqlPooling": true,
      "connectionTimeoutSeconds": 15,
      "commandTimeoutSeconds": 30,
      "encrypted": true,
      "databases": [
        {
          "databaseName": "appdb",
          "users": [
            {
              "username": "app_owner",
              "password": "<encrypted-or-plain>",
              "encrypted": true,
              "roles": [
                "db_owner"
              ],
              "connectionString": "Host=pg01.contoso.local;Database=appdb;Username=app_owner;Password=********;Ssl Mode=Require;Port=5432;Timeout=15;Command Timeout=30;Pooling=true;"
            }
          ]
        }
      ]
    }
  ],
  "trash": []
}
```

Config notes:

- `selectedServerName` stores the selected connection identifier, not just the host name
- `serverIdentifier` is a generated connection id used by selection and TUI workflows
- `displayName` is the human-readable label shown in the UI
- Top-level `timeouts` provide defaults; per-server timeout values override them
- New SQL Server connections default to port `1433`, admin database `master`, and trust mode `false`
- New PostgreSQL connections default to port `5432`, admin database `postgres`, SSL mode `prefer`, and pooling `true`
- New MySQL/MariaDB connections default to port `3306`, admin database `mysql`, SSL mode `required`, pooling `true`, and `allowPublicKeyRetrieval` `false`
- `versionHistory` metadata is generated for servers, databases, users, and trash entries even though it is omitted from the example above for brevity
- The TUI trash bin stores deleted server, database, and user config entries so they can be restored later
- Generic roles in config are provider-aware at execution time: SQL Server supports `db_owner`, `db_datareader`, and `db_datawriter`; PostgreSQL and MySQL/MariaDB support `db_owner` only

## CLI Commands

Current top-level commands:

- `version`
- `tui`
- `view-config`
- `init-config`
- `add-server`
- `select-server`
- `sync-server`
- `show-databases`
- `create-database`
- `remove-database`
- `create-user`
- `add-role`
- `remove-role`
- `show-users`
- `test-user-login`
- `remove-user`
- `update-password`
- `enable-config-encryption`
- `disable-config-encryption`
- `migrate-config-encryption-format`
- `help`

Compatibility notes:

- `sql-manager --version` is supported in addition to `sql-manager version`
- PowerShell-style compatibility is still supported, for example `--action CreateUser`
- Many server-scoped commands can reuse the selected connection and stored admin credentials from config when those options are omitted

## Examples

Add a SQL Server connection:

```powershell
.\sql-manager.exe add-server --display-name "Primary SQL" --server-name sql01.contoso.local --provider sqlserver --port 1433 --admin-database master --admin-username sa --admin-password "Secret123!" --trust-mode false
```

Add a PostgreSQL connection:

```powershell
.\sql-manager.exe add-server --display-name "Primary Postgres" --server-name pg01.contoso.local --provider postgresql --port 5432 --admin-database postgres --admin-username postgres --admin-password "Secret123!" --ssl-mode require --pooling true
```

Add an Azure MySQL Flexible Server connection:

```powershell
.\sql-manager.exe add-server --display-name "Azure MySQL" --server-name mysql-flex-prod.mysql.database.azure.com --provider mysql --port 3306 --admin-database mysql --admin-username sqlmanager --admin-password "Secret123!" --mysql-ssl-mode required --pooling true --allow-public-key-retrieval false
```

Add a local MariaDB connection:

```powershell
.\sql-manager.exe add-server --display-name "Local MariaDB" --server-name localhost --provider mariadb --port 3306 --admin-database mysql --admin-username root --admin-password "Secret123!" --mysql-ssl-mode preferred --pooling true --allow-public-key-retrieval true
```

Select the active connection by identifier:

```powershell
.\sql-manager.exe select-server --server-identifier 2
```

Sync the selected server into config:

```powershell
.\sql-manager.exe sync-server --admin-password "Secret123!"
```

Create a database:

```powershell
.\sql-manager.exe create-database --server-name sql01.contoso.local --admin-username sa --admin-password "Secret123!" --database-name LabDB
```

Create a SQL Server user with generated password and two roles:

```powershell
.\sql-manager.exe create-user --server-name sql01.contoso.local --admin-username sa --admin-password "Secret123!" --database-name LabDB --user-name LabDBUser --roles db_owner,db_datareader
```

Remove a role from a user:

```powershell
.\sql-manager.exe remove-role --server-name sql01.contoso.local --admin-username sa --admin-password "Secret123!" --database-name LabDB --user-name LabDBUser --roles db_datareader
```

Test a stored or supplied user login:

```powershell
.\sql-manager.exe test-user-login --server-name pg01.contoso.local --database-name appdb --user-name app_owner --user-password "Secret123!"
```

Enable config encryption:

```powershell
.\sql-manager.exe enable-config-encryption --config-path .\sql-config.json --encryption-password "ComplexPass!123"
```

Migrate an older encrypted config to the current full-file encrypted format:

```powershell
.\sql-manager.exe migrate-config-encryption-format --config-path .\sql-config.json --encryption-password "ComplexPass!123"
```

Disable config encryption:

```powershell
.\sql-manager.exe disable-config-encryption --config-path .\sql-config.json --encryption-password "ComplexPass!123"
```

## MySQL / MariaDB Notes

- Azure Database for MySQL Flexible Server: use the server FQDN, port `3306`, the admin login shown in Azure, and keep TLS on with `--mysql-ssl-mode required` or `verifyfull`. Leave `--allow-public-key-retrieval false`.
- Local MySQL or MariaDB: use the local host or VM host name, keep `--mysql-ssl-mode preferred` when the server negotiates TLS, or switch to `disabled` only if the server is intentionally running without TLS. `--allow-public-key-retrieval true` can be needed for local MySQL 8 password auth without TLS.
- SQL Manager manages MySQL/MariaDB accounts as `'username'@'%'` so the generated login can connect from Azure clients and local tools unless you later tighten the host part manually.
- The MySQL/MariaDB `db_owner` mapping is `GRANT ALL PRIVILEGES ON db.* TO 'username'@'%';`. That is the closest equivalent to the SQL Server-style full-database owner role this tool manages.
- To create users and grant that access, the admin account you configure for MySQL/MariaDB needs privileges to run `CREATE USER`, `ALTER USER`, `DROP USER`, and `GRANT` on the target databases. Azure Flexible Server admin accounts have the needed rights for this workflow.

## TUI Overview

Running `sql-manager` with no arguments opens the full-screen terminal UI.

Current TUI areas include:

- Server management: select active server, add or edit a connection, sync configuration, and show or manage databases
- User management: create users, manage roles, show users, test user logins, remove users, and update passwords
- Configuration menu: save, view config, initialize config, change theme, toggle password encryption, inspect the trash bin, and refresh from disk
- Help and About views: version details, command reference, repository URL, and general navigation help

## Build and Test

Run validation locally:

```powershell
.\test.ps1 -Configuration Release
```

Build debug output:

```powershell
.\build.ps1 -Target Debug
```

Build release output:

```powershell
.\build.ps1 -Target Release
```

Publish self-contained production output for selected runtimes:

```powershell
.\build.ps1 -Target Prod -Runtime win-x64,linux-x64
```

Publish the full runtime matrix:

```powershell
.\build.ps1 -Target Prod -Runtime All
```

Current publish targets supported by `build.ps1`:

- `win-x64`
- `win-arm64`
- `osx-x64`
- `osx-arm64`
- `linux-x64`
- `linux-arm64`
- `linux-musl-x64`
- `linux-musl-arm64`

Build outputs are written under:

```text
artifacts\debug
artifacts\release
artifacts\prod\<runtime>
```

Versioning notes:

- `build.ps1` increments `build-number.txt` when `-VersionOverride` is not supplied
- `-BuildNumber` and `-VersionOverride` are mutually exclusive
- `-InformationalVersionOverride` lets you keep a stable numeric version while appending metadata such as a commit hash

## GitHub Releases

The repository includes `.github/workflows/release.yml` to publish release assets from Git tags.

The workflow:

- runs on every pushed tag
- requires the tag to be a numeric version like `1.0.0` or `1.0.0.1`
- uses `actions/checkout@v5` and `actions/setup-dotnet@v5` so the release pipeline is aligned with the GitHub Actions Node.js 24 transition
- runs `.\test.ps1 -Configuration Release`
- publishes self-contained binaries for the full runtime matrix
- zips each runtime folder into a release asset
- creates or updates the GitHub release and uploads the generated archives

Release builds use the tag as the numeric version and append the short commit SHA to the informational version. For example, tag `1.0.0` built from commit `abcdef1...` becomes informational version `1.0.0+abcdef1`.

GitHub Actions note:

- GitHub will force JavaScript actions to run on Node.js 24 by default on June 2, 2026, and remove Node.js 20 from hosted runners on September 16, 2026
- this repository already uses the Node.js 24-ready major versions of the GitHub-maintained actions in `release.yml`
- on self-hosted runners, `actions/setup-dotnet@v5` requires a runner version that supports Node.js 24, so keep the runner updated rather than relying on the temporary `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24` or `ACTIONS_ALLOW_USE_UNSECURE_NODE_VERSION` overrides

## Notes

- The app defaults to TUI mode when started without arguments and switches to CLI mode when arguments are supplied
- SQL Server role aliases accepted by the CLI include `dbowner`, `db_owner`, `dbreader`, `db_reader`, `db_datareader`, `dbwriter`, `db_writer`, and `db_datawriter`
- PostgreSQL and MySQL/MariaDB currently support `db_owner` only; reader and writer role aliases are rejected for those targets
- Existing encrypted configs from older versions remain readable and can be migrated to the full-file encrypted format
