using Spectre.Console;

namespace SqlManager;

internal enum PromptNavigation
{
    Submit,
    Back,
    Cancel
}

internal readonly record struct PromptResponse<T>(PromptNavigation Navigation, T? Value)
{
    public static PromptResponse<T> Submitted(T value) => new(PromptNavigation.Submit, value);

    public static PromptResponse<T> Back() => new(PromptNavigation.Back, default);

    public static PromptResponse<T> Cancel() => new(PromptNavigation.Cancel, default);
}

internal sealed class TerminalUi
{
    private const string BackChoice = "<Back>";
    private const string CancelChoice = "<Cancel>";
    private readonly IAnsiConsole _console;

    public TerminalUi(IAnsiConsole console)
    {
        _console = console;
    }

    public bool CanPromptInteractively => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public void WriteSuccess(string message) => _console.MarkupLine($"[green]{Markup.Escape(message)}[/]");

    public void WriteInfo(string message) => _console.MarkupLine($"[deepskyblue1]{Markup.Escape(message)}[/]");

    public void WriteWarning(string message) => _console.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");

    public void WriteError(string message) => _console.MarkupLine($"[red]{Markup.Escape(message)}[/]");

    public void WriteFatal(string message) => _console.MarkupLine($"[bold red]{Markup.Escape(message)}[/]");

    public void RenderResult(OperationResult result)
    {
        if (result.Succeeded)
        {
            WriteSuccess(result.Message);
        }
        else
        {
            WriteError(result.Message);
        }

        foreach (var detail in result.Details)
        {
            WriteInfo(detail);
        }
    }

    public void RenderUsers(IReadOnlyList<DatabaseUserRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("UserName").AddColumn("LoginName").AddColumn("Roles");
        foreach (var row in rows)
        {
            table.AddRow(row.UserName, row.LoginName, row.Roles);
        }

        _console.Write(table);
    }

    public void RenderDatabases(IReadOnlyList<string> databaseNames)
    {
        if (databaseNames.Count == 0)
        {
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Database");
        foreach (var databaseName in databaseNames)
        {
            table.AddRow(databaseName);
        }

        _console.Write(table);
    }

    public void RenderConfigSummary(SqlManagerConfig config, string configPath)
    {
        var totalDatabases = config.Servers.Sum(server => server.Databases.Count);
        var totalUsers = config.Servers.Sum(server => server.Databases.Sum(database => database.Users.Count));
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("Config Path", configPath);
        grid.AddRow("Selected Server", string.IsNullOrWhiteSpace(config.SelectedServerName) ? "<none>" : config.SelectedServerName);
        grid.AddRow("Configured Servers", config.Servers.Count.ToString());
        grid.AddRow("Tracked Databases", totalDatabases.ToString());
        grid.AddRow("Tracked Users", totalUsers.ToString());

        _console.Write(new Panel(grid)
            .Header($"SQL Manager v{AppVersion.DisplayVersion}")
            .RoundedBorder()
            .BorderColor(Color.Grey));
    }

    public void RenderConfiguredServers(SqlManagerConfig config, string? activeServer)
    {
        if (config.Servers.Count == 0)
        {
            WriteWarning("No servers are configured.");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Active")
            .AddColumn("Server")
            .AddColumn("Admin User")
            .AddColumn("Databases")
            .AddColumn("Users");

        foreach (var server in config.Servers)
        {
            var isActive = !string.IsNullOrWhiteSpace(activeServer)
                && server.ServerName.Equals(activeServer, StringComparison.OrdinalIgnoreCase);
            table.AddRow(
                isActive ? "*" : string.Empty,
                server.ServerName,
                string.IsNullOrWhiteSpace(server.AdminUsername) ? "<none>" : server.AdminUsername,
                server.Databases.Count.ToString(),
                server.Databases.Sum(database => database.Users.Count).ToString());
        }

        _console.Write(new Panel(table)
            .Header("Configured Servers")
            .RoundedBorder()
            .BorderColor(Color.Grey));
    }

    public PromptResponse<string> PromptTextWithNavigation(string prompt, string? defaultValue = null, bool allowEmpty = false)
    {
        var textPrompt = new TextPrompt<string>($"{Markup.Escape(prompt)} [grey](type :back or :cancel)[/]");
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            textPrompt.DefaultValue(defaultValue);
            textPrompt.ShowDefaultValue();
        }

        if (allowEmpty)
        {
            textPrompt.AllowEmpty();
        }

        textPrompt.Validate(value =>
        {
            if (IsNavigationCommand(value))
            {
                return ValidationResult.Success();
            }

            if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            {
                return ValidationResult.Error("Value is required.");
            }

            return ValidationResult.Success();
        });

        var value = _console.Prompt(textPrompt);
        if (IsBackCommand(value))
        {
            return PromptResponse<string>.Back();
        }

        if (IsCancelCommand(value))
        {
            return PromptResponse<string>.Cancel();
        }

        return PromptResponse<string>.Submitted(value);
    }

    public PromptResponse<string> PromptSecretWithNavigation(string prompt, bool allowEmpty = false)
    {
        var secretPrompt = new TextPrompt<string>($"{Markup.Escape(prompt)} [grey](type :back or :cancel)[/]").Secret();
        if (allowEmpty)
        {
            secretPrompt.AllowEmpty();
        }

        secretPrompt.Validate(value =>
        {
            if (IsNavigationCommand(value))
            {
                return ValidationResult.Success();
            }

            if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            {
                return ValidationResult.Error("Value is required.");
            }

            return ValidationResult.Success();
        });

        var value = _console.Prompt(secretPrompt);
        if (IsBackCommand(value))
        {
            return PromptResponse<string>.Back();
        }

        if (IsCancelCommand(value))
        {
            return PromptResponse<string>.Cancel();
        }

        return PromptResponse<string>.Submitted(value);
    }

    public PromptResponse<string> PromptSelectionWithNavigation(string title, IEnumerable<string> choices, bool includeBack = true, bool includeCancel = true)
    {
        var allChoices = choices.ToList();
        if (includeBack)
        {
            allChoices.Add(BackChoice);
        }

        if (includeCancel)
        {
            allChoices.Add(CancelChoice);
        }

        var prompt = new SelectionPrompt<string>().Title(title).AddChoices(allChoices);
        var selected = _console.Prompt(prompt);
        return selected switch
        {
            BackChoice => PromptResponse<string>.Back(),
            CancelChoice => PromptResponse<string>.Cancel(),
            _ => PromptResponse<string>.Submitted(selected)
        };
    }

    public PromptResponse<bool> PromptConfirmWithNavigation(string prompt, bool defaultValue = true)
    {
        var yes = "Yes";
        var no = "No";
        var choices = defaultValue
            ? new[] { yes, no }
            : new[] { no, yes };
        var selected = PromptSelectionWithNavigation(prompt, choices);
        return selected.Navigation switch
        {
            PromptNavigation.Back => PromptResponse<bool>.Back(),
            PromptNavigation.Cancel => PromptResponse<bool>.Cancel(),
            _ => PromptResponse<bool>.Submitted(string.Equals(selected.Value, yes, StringComparison.OrdinalIgnoreCase))
        };
    }

    public string PromptText(string prompt, string? defaultValue = null, bool allowEmpty = false)
    {
        var textPrompt = new TextPrompt<string>(prompt);
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            textPrompt.DefaultValue(defaultValue);
            textPrompt.ShowDefaultValue();
        }

        if (allowEmpty)
        {
            textPrompt.AllowEmpty();
        }
        else
        {
            textPrompt.Validate(value => !string.IsNullOrWhiteSpace(value)
                ? ValidationResult.Success()
                : ValidationResult.Error("Value is required."));
        }

        return _console.Prompt(textPrompt);
    }

    public string PromptSecret(string prompt, bool allowEmpty = false)
    {
        var secretPrompt = new TextPrompt<string>(prompt).Secret();
        if (allowEmpty)
        {
            secretPrompt.AllowEmpty();
        }
        else
        {
            secretPrompt.Validate(value => !string.IsNullOrWhiteSpace(value)
                ? ValidationResult.Success()
                : ValidationResult.Error("Value is required."));
        }

        return _console.Prompt(secretPrompt);
    }

    public string PromptSelection(string title, IEnumerable<string> choices)
    {
        var prompt = new SelectionPrompt<string>().Title(title).AddChoices(choices);
        return _console.Prompt(prompt);
    }

    public bool Confirm(string prompt, bool defaultValue = true)
        => _console.Confirm(prompt, defaultValue);

    public void Pause()
    {
        if (!CanPromptInteractively)
        {
            return;
        }

        _console.MarkupLine("[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }

    public void WriteVersion()
    {
        WriteInfo($"sql-manager {AppVersion.DisplayVersion}");
    }

    public void WriteHelp(string defaultConfigPath)
    {
        _console.MarkupLine($"[grey]sql-manager {Markup.Escape(AppVersion.DisplayVersion)}[/]");
        _console.Write(new Rule("Commands"));
        WritePlainLine("  sql-manager version");
        WritePlainLine("  sql-manager --version");
        WritePlainLine("  sql-manager tui");
        WritePlainLine("  sql-manager view-config --config-path <path>");
        WritePlainLine("  sql-manager init-config --config-path <path> --server-name <server> --admin-username <user> [--admin-password <password>]");
        WritePlainLine("  sql-manager add-server --server-name <server> --admin-username <user> [--admin-password <password>]");
        WritePlainLine("  sql-manager select-server --server-name <server>");
        WritePlainLine("  sql-manager sync-server --server-name <server> --admin-username <user> --admin-password <password>");
        WritePlainLine("  sql-manager show-databases --admin-password <password>");
        WritePlainLine("  sql-manager create-database --database-name <database> --admin-password <password>");
        WritePlainLine("  sql-manager remove-database --database-name <database> --admin-password <password>");
        WritePlainLine("  sql-manager create-user --database-name <database> --user-name <user> --roles db_owner --admin-password <password> [--new-user-password <password>]");
        WritePlainLine("  sql-manager add-role --database-name <database> --user-name <user> --roles db_reader --admin-password <password>");
        WritePlainLine("  sql-manager remove-role --database-name <database> --user-name <user> --roles db_reader --admin-password <password>");
        WritePlainLine("  sql-manager show-users --database-name <database> --admin-password <password>");
        WritePlainLine("  sql-manager remove-user --user-name <user> --database-name <database> [--database-name <database>] [--remove-server-login] --removal-scope Database|Server|Both --admin-password <password>");
        WritePlainLine("  sql-manager update-password --user-name <user> --admin-password <password> [--new-user-password <password>]");
        WritePlainLine("  sql-manager help");
        _console.MarkupLine($"[grey]Default config path: {Markup.Escape(defaultConfigPath)}[/]");
        _console.MarkupLine("[grey]PowerShell-style compatibility is also supported: --action CreateUser or -Action CreateUser.[/]");
    }

    private static void WritePlainLine(string text)
    {
        Console.WriteLine(text);
    }

    private static bool IsBackCommand(string value)
        => string.Equals(value.Trim(), ":back", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancelCommand(string value)
        => string.Equals(value.Trim(), ":cancel", StringComparison.OrdinalIgnoreCase);

    private static bool IsNavigationCommand(string value)
        => IsBackCommand(value) || IsCancelCommand(value);
}
