using MyRPA.Workflow.Values;
using static MyRPA.Database.Tests.DatabaseHost;

namespace MyRPA.Database.Tests;

/// <summary>
/// One database engine as the scenarios need it: plugin settings for a read-write connection "main" and a read-only one
/// "ro" on the same database, the password (null when the connection string needs none) and the engine's DDL.
/// </summary>
public abstract class DatabaseScenarios
{
    private static int _tables;

    /// <summary>The password the connections need (null: none).</summary>
    protected abstract string? Password { get; }

    /// <summary>A statement that runs for about five seconds, or null when the engine has none.</summary>
    protected abstract string? Sleep { get; }

    private string Login => Password is null ? string.Empty : "\"password\": \"pwd\"";

    /// <summary>The settings, or a skip when the engine is not available here.</summary>
    protected abstract Dictionary<string, string> Settings();

    /// <summary>CREATE TABLE for (id int, name text, amount decimal(10,2), paid boolean, created timestamp with zone).</summary>
    protected abstract string CreateTable(string table);

    protected static string NewTable() => $"t{Environment.ProcessId}_{Interlocked.Increment(ref _tables)}";

    /// <summary>Skips a test whose server is not configured; in CI (MYRPA_TEST_REQUIRE_SERVERS=1) fails it instead.</summary>
    protected static void Unavailable(string reason)
    {
        if (Environment.GetEnvironmentVariable("MYRPA_TEST_REQUIRE_SERVERS") == "1")
        {
            Assert.Fail(reason);
        }

        Assert.Skip(reason);
    }

    private Dictionary<string, object?> Inputs(params (string Name, object? Value)[] values)
    {
        var inputs = values.ToDictionary(v => v.Name, v => v.Value);
        if (Password is not null)
        {
            inputs["pwd"] = Password;
        }

        return inputs;
    }

    private string Run(string type, string sql, string? extra = null, string connection = "main")
    {
        var more = string.Join(", ", new[] { extra, Login }.Where(s => !string.IsNullOrEmpty(s)));
        return Node(type, connection, sql, more.Length > 0 ? more : null);
    }

    private async Task<DatabaseHost> CreatedAsync(string table, params (string Name, string Value)[] extra)
    {
        var settings = Settings();
        foreach (var (name, value) in extra)
        {
            settings[name] = value;
        }

        var host = await StartAsync(settings);
        AssertSucceeded(await host.RunAsync(Run("Db.Execute", CreateTable(table)), inputs: Inputs()));
        return host;
    }

    [Fact]
    public async Task Parameters_CarryData_NeverSql()
    {
        var table = NewTable();
        await using var host = await CreatedAsync(table);
        const string hostile = "O'Brien'); DROP TABLE x; --";

        var result = await host.RunAsync(
            Run("Db.Execute", $"INSERT INTO {table} (id, name) VALUES (@id, @name)", "\"parameters\": \"{ 'id': 1, 'name': hostile }\", \"result\": \"inserted\"") + "," +
            Run("Db.Execute", $"INSERT INTO {table} (id, name) VALUES (@id, @name)", "\"parameters\": \"{ 'id': 2, 'name': 'plain' }\"") + "," +
            Run("Db.Query", $"SELECT id, name FROM {table} WHERE name = @name", "\"parameters\": \"{ 'name': hostile }\", \"result\": \"rows\"") + "," +
            Run("Db.Scalar", $"SELECT COUNT(*) FROM {table}", "\"result\": \"count\""),
            ["inserted", "rows", "count"],
            Inputs(("hostile", hostile)));

        AssertSucceeded(result);
        Assert.Equal(1L, result.Outputs["inserted"]);
        var row = Assert.Single((IReadOnlyList<object?>)result.Outputs["rows"]!);
        Assert.Equal(hostile, ((IReadOnlyDictionary<string, object?>)row!)["name"]);
        Assert.Equal(2L, result.Outputs["count"]);
    }

    [Fact]
    public async Task Values_RoundTrip_AsWorkflowValues()
    {
        var table = NewTable();
        await using var host = await CreatedAsync(table);
        var created = new DateTimeOffset(2026, 10, 9, 8, 30, 0, TimeSpan.Zero);

        var result = await host.RunAsync(
            Run("Db.Execute", $"INSERT INTO {table} (id, name, amount, paid, created) VALUES (@id, @name, @amount, @paid, @created)", "\"parameters\": \"values\"") + "," +
            Run("Db.Execute", $"INSERT INTO {table} (id, name) VALUES (@id, @name)", "\"parameters\": \"{ 'id': 2, 'name': null }\"") + "," +
            Run("Db.Query", $"SELECT id, name, amount, paid, created FROM {table} ORDER BY id", "\"result\": \"rows\""),
            ["rows"],
            Inputs(("values", WorkflowValues.Dictionary([new("id", 1L), new("name", "Zoë €"), new("amount", 1234.5m), new("paid", true), new("created", created)]))));

        AssertSucceeded(result);
        var rows = ((IReadOnlyList<object?>)result.Outputs["rows"]!).Cast<IReadOnlyDictionary<string, object?>>().ToList();
        Assert.Equal(1L, rows[0]["id"]);
        Assert.Equal("Zoë €", rows[0]["name"]);
        Assert.Equal(1234.5m, Convert.ToDecimal(rows[0]["amount"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(rows[0]["paid"] is true or 1L);
        Assert.Equal(created, rows[0]["created"] is DateTimeOffset date ? date : DateTimeOffset.Parse((string)rows[0]["created"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(rows[1]["name"]);
        Assert.Null(rows[1]["amount"]);
    }

    [Fact]
    public async Task Query_MoreRowsThanMaxRows_FailsTooManyItems()
    {
        var table = NewTable();
        await using var host = await CreatedAsync(table, ("maxRows", "3"));
        for (var i = 1; i <= 4; i++)
        {
            AssertSucceeded(await host.RunAsync(Run("Db.Execute", $"INSERT INTO {table} (id) VALUES (@id)", "\"parameters\": \"{ 'id': n }\""), inputs: Inputs(("n", (long)i))));
        }

        var result = await host.RunAsync(Run("Db.Query", $"SELECT id FROM {table}", "\"result\": \"rows\""), ["rows"], Inputs());

        AssertFailed(result, ErrorTypes.TooManyItems);
    }

    [Fact]
    public async Task ABadStatement_FailsDatabaseError_WithTheEnginesCode()
    {
        var table = NewTable();
        await using var host = await CreatedAsync(table);

        var result = await host.RunAsync(Run("Db.Query", $"SELECT no_such_column FROM {table}", "\"result\": \"rows\""), ["rows"], Inputs());

        AssertFailed(result, ErrorTypes.DatabaseError);
        Assert.Matches(@"\((SQL Server error \d+|PostgreSQL [0-9A-Z]{5}|SQLite error \d+)\)$", result.Error!.Message);
    }

    [Fact]
    public async Task AReadOnlyConnection_RefusesExecute_AndKeepsNoChange()
    {
        var table = NewTable();
        await using var host = await CreatedAsync(table);
        AssertSucceeded(await host.RunAsync(Run("Db.Execute", $"INSERT INTO {table} (id) VALUES (1)"), inputs: Inputs()));

        var execute = await host.RunAsync(Run("Db.Execute", $"DELETE FROM {table}", connection: "ro"), inputs: Inputs());
        var sneaky = await host.RunAsync(Run("Db.Scalar", $"INSERT INTO {table} (id) VALUES (99)", "\"result\": \"x\"", connection: "ro"), ["x"], Inputs());
        var count = await host.RunAsync(Run("Db.Scalar", $"SELECT COUNT(*) FROM {table}", "\"result\": \"n\"", connection: "ro"), ["n"], Inputs());

        AssertFailed(execute, ErrorTypes.ReadOnlyConnection);
        if (sneaky.Status == MyRPA.Core.Execution.ExecutionStatus.Failed)
        {
            Assert.Equal(ErrorTypes.ReadOnlyConnection, sneaky.Error!.ErrorType);
        }

        AssertSucceeded(count);
        Assert.Equal(1L, count.Outputs["n"]);
    }

    [Fact]
    public async Task AnUnknownConnection_FailsConnectionNotFound()
    {
        await using var host = await StartAsync(Settings());

        var result = await host.RunAsync(Node("Db.Scalar", "nope", "SELECT 1", "\"result\": \"x\""), ["x"]);

        AssertFailed(result, ErrorTypes.ConnectionNotFound);
        Assert.Contains("main", result.Error!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ 'bad name': 1 }")]
    [InlineData("{ 'list': [1, 2] }")]
    public async Task BadParameters_FailInvalidInput(string parameters)
    {
        await using var host = await StartAsync(Settings());

        var result = await host.RunAsync(Run("Db.Scalar", "SELECT 1", $"\"parameters\": \"{parameters}\", \"result\": \"x\""), ["x"], Inputs());

        AssertFailed(result, ErrorTypes.InvalidInput);
    }

    [Fact]
    public async Task ALongStatement_FailsTimeout()
    {
        if (Sleep is null)
        {
            Assert.Skip("This engine has no sleep statement.");
        }

        var settings = Settings();
        settings["commandTimeoutSeconds"] = "1";
        await using var host = await StartAsync(settings);

        var result = await host.RunAsync(Run("Db.Execute", Sleep!), inputs: Inputs());

        AssertFailed(result, ErrorTypes.Timeout);
    }

    [Fact]
    public async Task AWrongPassword_FailsDatabaseConnection_WithoutNamingIt_AndTheRightOneNeverLogs()
    {
        if (Password is null)
        {
            Assert.Skip("This engine's connection needs no password.");
        }

        await using var host = await StartAsync(Settings());
        var wrong = await host.RunAsync(Node("Db.Scalar", "main", "SELECT 1", "\"password\": \"pwd\", \"result\": \"x\""), ["x"], new Dictionary<string, object?> { ["pwd"] = "Wrong-Pa55word!" });
        var right = await host.RunAsync(Run("Db.Scalar", "SELECT 1", "\"result\": \"x\""), ["x"], Inputs());

        AssertFailed(wrong, ErrorTypes.DatabaseConnection);
        Assert.DoesNotContain("Wrong-Pa55word!", wrong.Error!.Message, StringComparison.Ordinal);
        AssertSucceeded(right);
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(Password!, StringComparison.Ordinal) || m.Contains("Wrong-Pa55word!", StringComparison.Ordinal));
    }
}

/// <summary>SQLite: always runs, against a database file in a temporary folder.</summary>
public sealed class SqliteTests : DatabaseScenarios, IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("myrpa-db-").FullName;

    protected override string? Password => null;

    protected override string? Sleep => null;

    private string DatabaseFile => Path.Combine(_folder, "test.db");

    protected override Dictionary<string, string> Settings() => new()
    {
        ["connection.main.provider"] = "Sqlite",
        ["connection.main.connectionString"] = $"Data Source={DatabaseFile};Pooling=False",
        ["connection.ro.provider"] = "Sqlite",
        ["connection.ro.connectionString"] = $"Data Source={DatabaseFile};Pooling=False",
        ["connection.ro.readOnly"] = "true",
    };

    protected override string CreateTable(string table) =>
        $"CREATE TABLE {table} (id INTEGER, name TEXT, amount NUMERIC, paid BOOLEAN, created TEXT)";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary folder left behind does not change any result.
        }
    }

    [Fact]
    public async Task APasswordInTheConnectionString_IsRefusedWhenThePluginLoads()
    {
        var settings = Settings();
        settings["connection.main.connectionString"] = $"Data Source={DatabaseFile};Password=hunter2";

        await using var host = await StartAsync(settings, expectFailure: true);

        Assert.True(host.Plugins.HasRequiredFailures);
        Assert.Contains(host.Plugins.Diagnostics, d => d.ToString()!.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(host.Plugins.Diagnostics, d => d.ToString()!.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APasswordWrittenInTheWorkflow_IsRefusedByTheLoader()
    {
        await using var host = await StartAsync(Settings());

        var load = host.Load(Node("Db.Scalar", "main", "SELECT 1", "\"password\": \"'hunter2'\", \"result\": \"x\""), ["x"], []);

        Assert.False(load.IsValid);
        Assert.Contains(load.Diagnostics, d => d.Code == "MYRPA1066");
    }

    [Fact]
    public async Task TheSql_IsLiteralText_NeverAnExpression()
    {
        await using var host = await StartAsync(Settings());

        // An expression-looking SQL is sent exactly as written; the variable is never evaluated into it.
        var result = await host.RunAsync(Node("Db.Scalar", "main", "SELECT 'a' || name", "\"result\": \"x\""), ["x"], new Dictionary<string, object?> { ["name"] = "'; DROP TABLE t; --" });

        AssertFailed(result, ErrorTypes.DatabaseError);
        Assert.Contains("name", result.Error!.Message, StringComparison.Ordinal);
    }
}

/// <summary>SQL Server: runs when MYRPA_TEST_SQLSERVER (a connection string without the password) and MYRPA_TEST_SQLSERVER_PASSWORD are set.</summary>
public sealed class SqlServerTests : DatabaseScenarios
{
    private static readonly string? _connection = Environment.GetEnvironmentVariable("MYRPA_TEST_SQLSERVER");

    protected override string? Password => Environment.GetEnvironmentVariable("MYRPA_TEST_SQLSERVER_PASSWORD");

    protected override string? Sleep => "WAITFOR DELAY '00:00:05'";

    protected override Dictionary<string, string> Settings()
    {
        if (string.IsNullOrWhiteSpace(_connection))
        {
            Unavailable("No SQL Server: set MYRPA_TEST_SQLSERVER and MYRPA_TEST_SQLSERVER_PASSWORD (see plugins/MyRPA.Database/README.md).");
        }

        return new()
        {
            ["connection.main.provider"] = "SqlServer",
            ["connection.main.connectionString"] = _connection!,
            ["connection.ro.provider"] = "SqlServer",
            ["connection.ro.connectionString"] = _connection!,
            ["connection.ro.readOnly"] = "true",
        };
    }

    protected override string CreateTable(string table) =>
        $"CREATE TABLE {table} (id INT, name NVARCHAR(100), amount DECIMAL(10,2), paid BIT, created DATETIMEOFFSET)";
}

/// <summary>PostgreSQL: runs when MYRPA_TEST_POSTGRES (a connection string without the password) and MYRPA_TEST_POSTGRES_PASSWORD are set.</summary>
public sealed class PostgreSqlTests : DatabaseScenarios
{
    private static readonly string? _connection = Environment.GetEnvironmentVariable("MYRPA_TEST_POSTGRES");

    protected override string? Password => Environment.GetEnvironmentVariable("MYRPA_TEST_POSTGRES_PASSWORD");

    protected override string? Sleep => "SELECT pg_sleep(5)";

    protected override Dictionary<string, string> Settings()
    {
        if (string.IsNullOrWhiteSpace(_connection))
        {
            Unavailable("No PostgreSQL: set MYRPA_TEST_POSTGRES and MYRPA_TEST_POSTGRES_PASSWORD (see plugins/MyRPA.Database/README.md).");
        }

        return new()
        {
            ["connection.main.provider"] = "PostgreSql",
            ["connection.main.connectionString"] = _connection!,
            ["connection.ro.provider"] = "PostgreSql",
            ["connection.ro.connectionString"] = _connection!,
            ["connection.ro.readOnly"] = "true",
        };
    }

    protected override string CreateTable(string table) =>
        $"CREATE TABLE {table} (id INT, name VARCHAR(100), amount DECIMAL(10,2), paid BOOLEAN, created TIMESTAMPTZ)";
}
