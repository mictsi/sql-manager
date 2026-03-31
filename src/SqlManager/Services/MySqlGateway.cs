using MySqlConnector;

namespace SqlManager;

internal sealed class MySqlGateway
{
    public async Task ExecuteNonQueryAsync(string server, int? port, string username, string password, string query, string database, SqlTimeoutConfig timeouts, string sslMode, bool pooling, bool allowPublicKeyRetrieval, CancellationToken cancellationToken)
    {
        await ExecuteWithCommandAsync(
            server,
            port,
            username,
            password,
            query,
            database,
            timeouts,
            sslMode,
            pooling,
            allowPublicKeyRetrieval,
            async command =>
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            cancellationToken);
    }

    public async Task<int> ExecuteScalarIntAsync(string server, int? port, string username, string password, string query, string database, SqlTimeoutConfig timeouts, string sslMode, bool pooling, bool allowPublicKeyRetrieval, CancellationToken cancellationToken)
    {
        var result = await ExecuteWithCommandAsync(
            server,
            port,
            username,
            password,
            query,
            database,
            timeouts,
            sslMode,
            pooling,
            allowPublicKeyRetrieval,
            async command => await command.ExecuteScalarAsync(cancellationToken),
            cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<string>> QueryNamesAsync(string server, int? port, string username, string password, string query, string database, SqlTimeoutConfig timeouts, string sslMode, bool pooling, bool allowPublicKeyRetrieval, CancellationToken cancellationToken)
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
            sslMode,
            pooling,
            allowPublicKeyRetrieval,
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

    public async Task<IReadOnlyList<DatabaseUserRow>> QueryDatabaseUsersAsync(string server, int? port, string username, string password, string database, string query, SqlTimeoutConfig timeouts, string sslMode, bool pooling, bool allowPublicKeyRetrieval, CancellationToken cancellationToken)
    {
        var rows = new List<DatabaseUserRow>();
        await ExecuteWithCommandAsync(
            server,
            port,
            username,
            password,
            query,
            database,
            timeouts,
            sslMode,
            pooling,
            allowPublicKeyRetrieval,
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
        string sslMode,
        bool pooling,
        bool allowPublicKeyRetrieval,
        Func<MySqlCommand, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(BuildAdminConnectionString(server, port, username, password, database, timeouts.ConnectionTimeoutSeconds, timeouts.CommandTimeoutSeconds, sslMode, pooling, allowPublicKeyRetrieval));
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCancellation.CancelAfter(TimeSpan.FromSeconds(timeouts.ConnectionTimeoutSeconds));

        try
        {
            await connection.OpenAsync(connectionCancellation.Token);
            await using var command = CreateCommand(query, connection, timeouts.CommandTimeoutSeconds);
            using var registration = cancellationToken.Register(static state => CancelCommand((MySqlCommand)state!), command);
            return await execute(command);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && connectionCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out connecting to MySQL '{server}' after {timeouts.ConnectionTimeoutSeconds} seconds.", exception);
        }
        catch (MySqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Operation cancelled.", exception, cancellationToken);
        }
        catch (InvalidOperationException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Operation cancelled.", exception, cancellationToken);
        }
    }

    private static MySqlCommand CreateCommand(string query, MySqlConnection connection, int commandTimeoutSeconds)
        => new(query, connection)
        {
            CommandTimeout = commandTimeoutSeconds
        };

    private static void CancelCommand(MySqlCommand command)
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

    private static string BuildAdminConnectionString(string server, int? port, string username, string password, string database, int connectionTimeoutSeconds, int commandTimeoutSeconds, string sslMode, bool pooling, bool allowPublicKeyRetrieval)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = server,
            Database = database,
            UserID = username,
            Password = password,
            SslMode = MySqlSslModes.ToMySqlSslMode(sslMode),
            ConnectionTimeout = (uint)connectionTimeoutSeconds,
            DefaultCommandTimeout = (uint)commandTimeoutSeconds,
            Pooling = pooling,
            AllowPublicKeyRetrieval = allowPublicKeyRetrieval
        };

        if (port is > 0)
        {
            builder.Port = (uint)port.Value;
        }

        return builder.ConnectionString;
    }
}
