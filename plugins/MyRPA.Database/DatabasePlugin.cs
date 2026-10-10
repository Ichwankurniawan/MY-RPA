using System.Data.Common;
using System.Globalization;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Database;

/// <summary>The database engines the plugin talks to.</summary>
public enum DatabaseProvider
{
    /// <summary>SQLite (Microsoft.Data.Sqlite).</summary>
    Sqlite,

    /// <summary>Microsoft SQL Server and Azure SQL (Microsoft.Data.SqlClient).</summary>
    SqlServer,

    /// <summary>PostgreSQL (Npgsql).</summary>
    PostgreSql,
}

/// <summary>A connection named in the plugin configuration; workflows refer to it by name only.</summary>
/// <param name="Name">The name workflows use.</param>
/// <param name="Provider">The engine.</param>
/// <param name="ConnectionString">The connection string, without a password (that is a secret property of the activity).</param>
/// <param name="ReadOnly">Whether nothing may change through this connection.</param>
public sealed record DatabaseConnection(string Name, DatabaseProvider Provider, string ConnectionString, bool ReadOnly);

/// <summary>Plugin settings, built once in <see cref="DatabasePlugin.Initialize"/>.</summary>
/// <param name="Connections">The named connections.</param>
/// <param name="CommandTimeoutSeconds">The timeout of one statement.</param>
/// <param name="MaxRows">The most rows one Db.Query returns.</param>
public sealed record DatabaseOptions(IReadOnlyDictionary<string, DatabaseConnection> Connections, int CommandTimeoutSeconds, int MaxRows);

/// <summary>
/// Entry point of the database plugin (Phase 7.1, ADR-0043). Settings:
/// <list type="bullet">
/// <item><c>connection.NAME.provider</c> — Sqlite, SqlServer or PostgreSql.</item>
/// <item><c>connection.NAME.connectionString</c> — without a password: the activities' secret <c>password</c> supplies it.</item>
/// <item><c>connection.NAME.readOnly</c> — true: Db.Execute is refused and Db.Query/Db.Scalar cannot change anything.</item>
/// <item><c>commandTimeoutSeconds</c> (30) and <c>maxRows</c> (10000).</item>
/// </list>
/// </summary>
public sealed class DatabasePlugin : IPlugin
{
    private DatabaseOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var connections = new Dictionary<string, DatabaseConnection>(StringComparer.OrdinalIgnoreCase);
        var names = settings.Keys
            .Select(k => k.Split('.'))
            .Where(p => p.Length == 3 && p[0].Equals("connection", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            string? Optional(string field) => settings.FirstOrDefault(s => s.Key.Equals($"connection.{name}.{field}", StringComparison.OrdinalIgnoreCase)).Value;
            string Required(string field) => Optional(field) ?? throw new ArgumentException($"Connection '{name}' needs the setting 'connection.{name}.{field}'.", nameof(context));
            var provider = Enum.TryParse<DatabaseProvider>(Required("provider"), ignoreCase: true, out var parsed)
                ? parsed
                : throw new ArgumentException($"'connection.{name}.provider' must be Sqlite, SqlServer or PostgreSql.", nameof(context));
            var connectionString = Required("connectionString");
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (builder.ContainsKey("Password") || builder.ContainsKey("Pwd"))
            {
                throw new ArgumentException($"'connection.{name}.connectionString' contains a password; give it at run time in the activity's secret 'password' instead.", nameof(context));
            }

            var readOnly = Optional("readOnly") is { } flag && bool.Parse(flag);
            connections[name] = new DatabaseConnection(name, provider, connectionString, readOnly);
        }

        _options = new DatabaseOptions(
            connections,
            (int)Number(settings, "commandTimeoutSeconds", 30, 1, 3600),
            (int)Number(settings, "maxRows", 10_000, 1, 1_000_000));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddActivity<DbQueryActivity>(DbQueryActivity.Descriptor)
            .AddActivity<DbExecuteActivity>(DbExecuteActivity.Descriptor)
            .AddActivity<DbScalarActivity>(DbScalarActivity.Descriptor);
    }

    private static long Number(IReadOnlyDictionary<string, string> settings, string name, long defaultValue, long min, long max)
    {
        if (!settings.TryGetValue(name, out var text))
        {
            return defaultValue;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new ArgumentException($"Setting '{name}' must be a whole number from {min} to {max}, not '{text}'.", nameof(settings));
    }
}
