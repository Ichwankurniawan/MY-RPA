using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using Npgsql;

namespace MyRPA.Database;

/// <summary><c>Db.Query</c>: the rows of a query as a table (ADR-0043).</summary>
public sealed class DbQueryActivity(DatabaseOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Db.Query"),
        "Query Database",
        Db.Category,
        "Runs a query on a connection named in the database plugin's configuration and returns its rows as a table (a List of Dictionaries keyed by column). The SQL is fixed text written in the workflow, never built from data; values go in only as named parameters (@name). Fails with TooManyItems above the plugin's maxRows.",
        [.. Db.Common(), new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the rows (a List of Dictionaries).") { ValueType = ActivityValueType.List }])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await Db.RunAsync(context, options, write: false, async (command, cancellationToken) =>
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var names = Db.ColumnNames(reader);
                var result = new List<object?>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (result.Count == options.MaxRows)
                    {
                        throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"The query of {context.Node.Type} '{context.Node.Id}' returns more than {options.MaxRows} rows (setting maxRows); narrow it.");
                    }

                    result.Add(WorkflowValues.Dictionary(names.Select((name, i) => new KeyValuePair<string, object?>(name, Db.Value(reader, i)))));
                }

                return result;
            }
        }).ConfigureAwait(false);

        context.SetValue(context.GetName("result"), WorkflowValues.List(rows));
        return ActivityResult.Completed;
    }
}

/// <summary><c>Db.Execute</c>: INSERT, UPDATE, DELETE or DDL; returns the rows affected (ADR-0043).</summary>
public sealed class DbExecuteActivity(DatabaseOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Db.Execute"),
        "Execute SQL",
        Db.Category,
        "Runs a statement that changes data (INSERT, UPDATE, DELETE) on a connection named in the database plugin's configuration and returns the number of rows affected. The SQL is fixed text; values go in only as named parameters (@name). Refused on a read-only connection.",
        [.. Db.Common(), new("result", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the number of rows affected.") { ValueType = ActivityValueType.Int }])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var affected = await Db.RunAsync(context, options, write: true, async (command, cancellationToken) =>
            (long)await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        if (context.HasProperty("result"))
        {
            context.SetValue(context.GetName("result"), affected);
        }

        return ActivityResult.Completed;
    }
}

/// <summary><c>Db.Scalar</c>: the first column of the first row (ADR-0043).</summary>
public sealed class DbScalarActivity(DatabaseOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Db.Scalar"),
        "Query One Value",
        Db.Category,
        "Runs a query and returns the first column of its first row (null when there is no row), for example a count. The SQL is fixed text; values go in only as named parameters (@name).",
        [.. Db.Common(), new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the value.") { ValueType = ActivityValueType.Any }])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var value = await Db.RunAsync(context, options, write: false, async (command, cancellationToken) =>
        {
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0 ? Db.Value(reader, 0) : null;
            }
        }).ConfigureAwait(false);

        context.SetValue(context.GetName("result"), value);
        return ActivityResult.Completed;
    }
}

/// <summary>The errorType values of the database activities (ADR-0043).</summary>
public static class DatabaseErrorTypes
{
    /// <summary>No connection with that name in the plugin configuration.</summary>
    public const string ConnectionNotFound = "ConnectionNotFound";

    /// <summary>The database could not be reached, or refused the login.</summary>
    public const string DatabaseConnection = "DatabaseConnection";

    /// <summary>The database refused the statement (syntax, a constraint, a missing table…).</summary>
    public const string DatabaseError = "DatabaseError";

    /// <summary>Db.Execute on a read-only connection, or a change attempted through one.</summary>
    public const string ReadOnlyConnection = "ReadOnlyConnection";

    /// <summary>The statement did not finish within the command timeout.</summary>
    public const string Timeout = "Timeout";
}

/// <summary>Connections, parameters, values and failure classification shared by the database activities.</summary>
internal static class Db
{
    public const string Category = "Database";

    public const string InvalidInput = "InvalidInput";

    /// <summary>connection, sql (literal text), parameters, username, password.</summary>
    public static IEnumerable<ActivityPropertyDefinition> Common() =>
    [
        new("connection", ActivityPropertyKind.Text, isRequired: true, "The name of a connection in the database plugin's configuration.") { ValueType = ActivityValueType.String },
        new("sql", ActivityPropertyKind.Text, isRequired: true, "The SQL, written as fixed text (never built from data). Refer to values as @name and give them in parameters.") { ValueType = ActivityValueType.String, IsMultiline = true },
        new("parameters", ActivityPropertyKind.Expression, isRequired: false, "The values for the SQL's @names: a Dictionary of name → value (text, number, true/false, date or null).") { ValueType = ActivityValueType.Dictionary },
        new("username", ActivityPropertyKind.Expression, isRequired: false, "The database user (default: the connection string's).") { ValueType = ActivityValueType.String },
        new("password", ActivityPropertyKind.Expression, isRequired: false, "The database password: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
    ];

    /// <summary>
    /// Opens the named connection, prepares the command with its parameters and runs <paramref name="run"/>. On a
    /// read-only connection a change is impossible: SQLite opens read-only, PostgreSQL runs a read-only session, and SQL
    /// Server's work is rolled back.
    /// </summary>
    public static async Task<T> RunAsync<T>(IActivityContext context, DatabaseOptions options, bool write, Func<DbCommand, CancellationToken, Task<T>> run)
    {
        var name = context.GetText("connection");
        if (!options.Connections.TryGetValue(name, out var definition))
        {
            var known = options.Connections.Count == 0 ? "none is configured" : "configured: " + string.Join(", ", options.Connections.Keys.Order(StringComparer.Ordinal));
            throw new ActivityFailedException(DatabaseErrorTypes.ConnectionNotFound, $"There is no database connection '{name}' ({known}).");
        }

        if (write && definition.ReadOnly)
        {
            throw new ActivityFailedException(DatabaseErrorTypes.ReadOnlyConnection, $"Connection '{name}' is read-only; {context.Node.Type} cannot change data through it.");
        }

        var sql = context.GetText("sql");
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ActivityFailedException(InvalidInput, $"'sql' of {context.Node.Type} '{context.Node.Id}' is empty.");
        }

        var parameters = Parameters(context);
        var user = context.HasProperty("username") ? context.Evaluate("username") as string ?? throw Invalid(context, "username", "text") : null;
        var password = context.HasProperty("password") ? context.Evaluate("password") as string ?? throw Invalid(context, "password", "text") : null;

        var timeout = TimeSpan.FromSeconds(options.CommandTimeoutSeconds);
        if (context.Deadline is { } deadline && deadline - context.TimeProvider.GetUtcNow() < timeout)
        {
            var remaining = deadline - context.TimeProvider.GetUtcNow();
            timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        }

        var seconds = Math.Ceiling(timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        using var timer = new CancellationTokenSource(timeout, context.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timer.Token);
        var cancellationToken = linked.Token;
        var opened = false;
        try
        {
            var connection = Create(definition, user, password);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                opened = true;
                DbTransaction? transaction = null;
                if (definition.ReadOnly && definition.Provider == DatabaseProvider.PostgreSql)
                {
                    var readOnly = connection.CreateCommand();
                    await using (readOnly.ConfigureAwait(false))
                    {
                        readOnly.CommandText = "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY";
                        await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (definition.ReadOnly && definition.Provider == DatabaseProvider.SqlServer)
                {
                    transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    var command = connection.CreateCommand();
                    await using (command.ConfigureAwait(false))
                    {
                        command.CommandText = sql;
                        command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds);
                        command.Transaction = transaction;
                        foreach (var (parameterName, value) in parameters)
                        {
                            var parameter = command.CreateParameter();
                            parameter.ParameterName = "@" + parameterName;
                            parameter.Value = value;
                            command.Parameters.Add(parameter);
                        }

                        return await run(command, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (transaction is not null)
                    {
                        // A read-only SQL Server connection never keeps a change.
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        await transaction.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new ActivityFailedException(DatabaseErrorTypes.Timeout, $"The statement on '{name}' did not finish within {seconds} s (setting commandTimeoutSeconds).", ex);
        }
        catch (DbException ex) when (context.CancellationToken.IsCancellationRequested)
        {
            // A driver may report a cancelled command as its own error; cancelling the run is not a database failure.
            throw new OperationCanceledException("The run was cancelled.", ex, context.CancellationToken);
        }
        catch (Exception ex) when (IsTimeout(ex) || (timer.IsCancellationRequested && ex is DbException or InvalidOperationException))
        {
            throw new ActivityFailedException(DatabaseErrorTypes.Timeout, $"The statement on '{name}' did not finish within {seconds} s (setting commandTimeoutSeconds).", ex);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException && !opened)
        {
            throw new ActivityFailedException(DatabaseErrorTypes.DatabaseConnection, $"Cannot connect to '{name}': {Describe(ex)}", ex);
        }
        catch (DbException ex) when (IsReadOnlyViolation(ex))
        {
            throw new ActivityFailedException(DatabaseErrorTypes.ReadOnlyConnection, $"Connection '{name}' is read-only: {Describe(ex)}", ex);
        }
        catch (DbException ex)
        {
            throw new ActivityFailedException(DatabaseErrorTypes.DatabaseError, $"The database refused the statement of {context.Node.Type} '{context.Node.Id}': {Describe(ex)}", ex);
        }
    }

    /// <summary>A connection for <paramref name="definition"/> with the run's user and password set.</summary>
    private static DbConnection Create(DatabaseConnection definition, string? user, string? password)
    {
        switch (definition.Provider)
        {
            case DatabaseProvider.SqlServer:
                var sql = new SqlConnectionStringBuilder(definition.ConnectionString);
                if (user is not null)
                {
                    sql.UserID = user;
                }

                if (password is not null)
                {
                    sql.Password = password;
                }

                return new SqlConnection(sql.ConnectionString);
            case DatabaseProvider.PostgreSql:
                var postgres = new NpgsqlConnectionStringBuilder(definition.ConnectionString);
                if (user is not null)
                {
                    postgres.Username = user;
                }

                if (password is not null)
                {
                    postgres.Password = password;
                }

                return new NpgsqlConnection(postgres.ConnectionString);
            default:
                var sqlite = new SqliteConnectionStringBuilder(definition.ConnectionString);
                if (definition.ReadOnly)
                {
                    sqlite.Mode = SqliteOpenMode.ReadOnly;
                }

                return new SqliteConnection(sqlite.ConnectionString);
        }
    }

    /// <summary>The parameters as database values; names are plain identifiers, values scalars.</summary>
    private static List<(string Name, object Value)> Parameters(IActivityContext context)
    {
        var result = new List<(string, object)>();
        if (!context.HasProperty("parameters"))
        {
            return result;
        }

        var map = context.Evaluate("parameters") as IReadOnlyDictionary<string, object?> ?? throw Invalid(context, "parameters", "a Dictionary of parameter names and values");
        foreach (var (name, value) in map)
        {
            if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                throw new ActivityFailedException(InvalidInput, $"Parameter '{name}' of {context.Node.Type} '{context.Node.Id}' must be a name of letters, digits and _ (used in the SQL as @{name}).");
            }

            result.Add((name, value switch
            {
                null => DBNull.Value,
                string or long or decimal or bool => value,
                DateTimeOffset date => date.ToUniversalTime(),
                _ => throw new ActivityFailedException(InvalidInput, $"Parameter '{name}' of {context.Node.Type} '{context.Node.Id}' must be text, a number, true/false, a date or null, not a List or Dictionary."),
            }));
        }

        return result;
    }

    /// <summary>Column names made unique (an empty or repeated name gets a number).</summary>
    public static List<string> ColumnNames(DbDataReader reader)
    {
        var names = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            name = string.IsNullOrWhiteSpace(name) ? $"column{i + 1}" : name;
            var unique = name;
            for (var n = 2; !used.Add(unique); n++)
            {
                unique = $"{name}_{n}";
            }

            names.Add(unique);
        }

        return names;
    }

    /// <summary>A column value as a workflow value (ADR-0009 canonical values).</summary>
    public static object? Value(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            string text => text,
            bool flag => flag,
            byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            ulong big => big <= long.MaxValue ? (long)big : (decimal)big,
            decimal number => number,
            double number when double.IsFinite(number) => (decimal)number,
            float number when float.IsFinite(number) => (decimal)number,
            DateTimeOffset date => date,
            DateTime date => new DateTimeOffset(DateTime.SpecifyKind(date, date.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : date.Kind)),
            DateOnly day => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            Guid id => id.ToString(),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static bool IsTimeout(Exception ex) =>
        ex is SqlException { Number: -2 } or NpgsqlException { InnerException: TimeoutException } or TimeoutException;

    private static bool IsReadOnlyViolation(DbException ex) =>
        ex is PostgresException { SqlState: "25006" } or SqliteException { SqliteErrorCode: 8 };

    /// <summary>The driver's message (one line, shortened) with the provider's error code; it never holds the password.</summary>
    private static string Describe(Exception ex)
    {
        var code = ex switch
        {
            SqlException sql => $" (SQL Server error {sql.Number})",
            PostgresException postgres => $" (PostgreSQL {postgres.SqlState})",
            SqliteException sqlite => $" (SQLite error {sqlite.SqliteErrorCode})",
            _ => string.Empty,
        };
        var text = ex is PostgresException pg ? pg.MessageText : ex.Message;
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return (line.Length > 300 ? line[..300] + "…" : line) + code;
    }

    private static ActivityFailedException Invalid(IActivityContext context, string name, string expected) =>
        new(InvalidInput, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");
}
