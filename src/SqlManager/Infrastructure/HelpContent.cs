namespace SqlManager;

internal static class HelpContent
{
    private static readonly string[] CommandLines =
    [
        "sql-manager version",
        "sql-manager --version",
        "sql-manager tui",
        "sql-manager view-config --config-path <path>",
        "sql-manager init-config --config-path <path> --server-name <server> --provider sqlserver|postgresql --admin-username <user> [--admin-password <password>] [--port <port>] [--admin-database <database>]",
        "sql-manager add-server --server-name <server> --provider sqlserver|postgresql --admin-username <user> [--admin-password <password>] [--port <port>] [--admin-database <database>]",
        "sql-manager select-server --server-name <server>",
        "sql-manager sync-server --server-name <server> --admin-username <user> --admin-password <password>",
        "sql-manager show-databases --admin-password <password>",
        "sql-manager create-database --database-name <database> --admin-password <password>",
        "sql-manager remove-database --database-name <database> --admin-password <password>",
        "sql-manager create-user --database-name <database> --user-name <user> --roles db_owner --admin-password <password> [--new-user-password <password>]",
        "sql-manager add-role --database-name <database> --user-name <user> --roles db_owner --admin-password <password>",
        "sql-manager remove-role --database-name <database> --user-name <user> --roles db_owner --admin-password <password>",
        "sql-manager show-users --database-name <database> --admin-password <password>",
        "sql-manager test-user-login --database-name <database> --user-name <user> [--user-password <password>]",
        "sql-manager remove-user --user-name <user> --database-name <database> [--database-name <database>] [--remove-server-login] --removal-scope Database|Server|Both --admin-password <password>",
        "sql-manager update-password --user-name <user> --admin-password <password> [--new-user-password <password>]",
        "sql-manager enable-config-encryption --config-path <path> [--encryption-password <password>]",
        "sql-manager disable-config-encryption --config-path <path> [--encryption-password <password>]",
        "sql-manager migrate-config-encryption-format --config-path <path> [--encryption-password <password>]",
        "sql-manager help"
    ];

    private static readonly string[] NoteLines =
    [
        "PowerShell-style compatibility is also supported: --action CreateUser or -Action CreateUser.",
        "Roles vary by provider: SQL Server supports db_owner, db_datareader, db_datawriter; PostgreSQL supports db_owner only.",
        "Encrypted configs can be unlocked for CLI operations with --encryption-password <password>."
    ];

    public static IReadOnlyList<string> GetCommandLines() => CommandLines;

    public static IReadOnlyList<string> GetNoteLines() => NoteLines;

    public static string BuildCommandReferenceText(string defaultConfigPath)
    {
        var lines = new List<string>
        {
            $"sql-manager {AppVersion.DisplayVersion}",
            string.Empty,
            "Commands"
        };

        lines.AddRange(CommandLines.Select(command => $"  {command}"));
        lines.Add(string.Empty);
        lines.Add($"Default config path: {defaultConfigPath}");
        lines.AddRange(NoteLines);
        return string.Join(Environment.NewLine, lines);
    }

    public static string BuildHelpOverviewText(string configPath)
        => string.Join(Environment.NewLine,
        [
            "SQL Manager Help",
            "",
            "Navigation",
            "- Use Up and Down to move within a column.",
            "- Use Left and Right to move across the main columns.",
            "- Use Tab to cycle focus and Enter to run the selected action.",
            "",
            "Menus",
            "- File: save the current config with Ctrl+S or exit with Ctrl+Q.",
            "- Configuration: view config, initialize config, toggle password encryption, inspect trash, and refresh.",
            "- Games: launch built-in terminal games such as Snake, Pong, and Tetris.",
            "- Help: open version details, this overview, or the full command reference.",
            "- About: review app metadata and copy the repository URL.",
            "",
            "Current Config Path",
            configPath,
            "",
            "Open Help > Command Reference for the full CLI command list."
        ]);

    public static string BuildVersionText()
        => string.Join(Environment.NewLine,
        [
            "SQL Manager",
            $"Version: {AppVersion.DisplayVersion}",
            $"Built: {AppVersion.BuildDate}",
            $"Repository: {AppVersion.RepositoryUrl}"
        ]);
}
