using Microsoft.Data.SqlClient;

namespace SqlManager;

internal sealed class SqlServerGateway
{
    public async Task ExecuteNonQueryAsync(string server, int? port, string username, string password, string query, string database, SqlTimeoutConfig timeouts, string trustMode, CancellationToken cancellationToken)
    {
        await ExecuteWithCommandAsync(
            server,
            port,
            username,
            password,
            query,
            database,
            timeouts,
            trustMode,
            async command =>
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            cancellationToken);
    }

    public async Task<int> ExecuteScalarIntAsync(string server, int? port, string username, string password, string query, string database, SqlTimeoutConfig timeouts, string trustMode, CancellationToken cancellationToken)
    {
        var result = await ExecuteWithCommandAsync(
            server,
            port,
            username,
            password,
            query,
            database,
            timeouts,
            trustMode,
            async command => await command.ExecuteScalarAsync(cancellationToken),
            cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<string>> QueryNamesAsync(string server, int? port, string username, string password, string query, string database, SqlTimeoutConfig timeouts, string trustMode, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        await ExecuteWithCommandAsync(
            server,
            port,
            username,
            password,
            query,
            database,
            timeouts,
            trustMode,
            async command =>
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(reader.GetString(0));
                }

                return 0;
            },
            cancellationToken);

        return results;
    }

    public async Task<IReadOnlyList<DatabaseUserRow>> QueryDatabaseUsersAsync(string server, int? port, string username, string password, string database, SqlTimeoutConfig timeouts, string trustMode, CancellationToken cancellationToken)
    {
        const string query = """
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
""";

        var rows = new List<DatabaseUserRow>();
        await ExecuteWithCommandAsync(
            server,
            port,
            username,
            password,
            query,
            database,
            timeouts,
            trustMode,
            async command =>
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new DatabaseUserRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2)));
                }

                return 0;
            },
            cancellationToken);

        return rows;
    }

    private static async Task<T> ExecuteWithCommandAsync<T>(
        string server,
        int? port,
        string username,
        string password,
        string query,
        string database,
        SqlTimeoutConfig timeouts,
        string trustMode,
        Func<SqlCommand, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new SqlConnection(BuildAdminConnectionString(server, port, username, password, database, timeouts.ConnectionTimeoutSeconds, trustMode));
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCancellation.CancelAfter(TimeSpan.FromSeconds(timeouts.ConnectionTimeoutSeconds));

        try
        {
            await connection.OpenAsync(connectionCancellation.Token);
            await using var command = CreateCommand(query, connection, timeouts.CommandTimeoutSeconds);
            using var registration = cancellationToken.Register(static state => CancelCommand((SqlCommand)state!), command);
            return await execute(command);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && connectionCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out connecting to SQL Server '{server}' after {timeouts.ConnectionTimeoutSeconds} seconds.", exception);
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Operation cancelled.", exception, cancellationToken);
        }
        catch (InvalidOperationException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Operation cancelled.", exception, cancellationToken);
        }
    }

    private static SqlCommand CreateCommand(string query, SqlConnection connection, int commandTimeoutSeconds)
        => new(query, connection)
        {
            CommandTimeout = commandTimeoutSeconds
        };

    private static void CancelCommand(SqlCommand command)
    {
        try
        {
            command.Cancel();
        }
        catch
        {
            // Best-effort cancellation must not throw.
        }
    }

    private static string BuildAdminConnectionString(string server, int? port, string username, string password, string database, int connectionTimeoutSeconds, string trustMode)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = ServerConnectionOptions.BuildSqlServerDataSource(server, port),
            InitialCatalog = database,
            UserID = username,
            Password = password,
            ConnectTimeout = connectionTimeoutSeconds,
            ConnectRetryCount = 0,
            MultipleActiveResultSets = false
        };
        SqlServerTrustModes.Apply(builder, trustMode);

        return builder.ConnectionString;
    }
}
