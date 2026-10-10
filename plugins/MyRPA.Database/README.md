# MyRPA.Database

Database activities (Phase 7.1, [ADR-0043](../../docs/adr/0043-more-enterprise-integrations.md)) for SQLite, SQL Server
and PostgreSQL. A product plugin: it references the Automation SDK and the drivers `Microsoft.Data.Sqlite` 10.0.12 and
`Microsoft.Data.SqlClient` 7.1.1 (MIT) and `Npgsql` 10.0.3 (PostgreSQL licence), which only this plugin may reference.
It runs in-process and is fully trusted (ADR-0015); its load context is not a sandbox.

## Settings (plugin configuration, ADR-0019)

Connections are named in the configuration; a workflow refers to a connection by name and never sees or builds a
connection string.

| Setting | Default | Meaning |
|---|---|---|
| `connection.NAME.provider` | — | `Sqlite`, `SqlServer` or `PostgreSql` |
| `connection.NAME.connectionString` | — | The connection string **without a password** (one with `Password` or `Pwd` is refused when the plugin loads) |
| `connection.NAME.readOnly` | false | true: `Db.Execute` is refused, and a change through `Db.Query` or `Db.Scalar` is impossible (below) |
| `commandTimeoutSeconds` | 30 | The timeout of one statement (also capped by the run's deadline) |
| `maxRows` | 10000 | The most rows one `Db.Query` returns |

```json
{ "directory": "plugins/MyRPA.Database/bin/Debug/net10.0",
  "settings": {
    "connection.erp.provider": "SqlServer",
    "connection.erp.connectionString": "Server=erp-sql;Database=Finance;User ID=robot;Encrypt=True",
    "connection.reports.provider": "PostgreSql",
    "connection.reports.connectionString": "Host=reports;Database=bi;Username=reader",
    "connection.reports.readOnly": "true" } }
```

## Activities

Each activity takes the same properties:

| Property | Kind | Meaning |
|---|---|---|
| `connection` | text | The connection's name |
| `sql` | **text, never an expression** | The SQL as written in the workflow; refer to values as `@name` |
| `parameters` | expression, Dictionary | `@name` → value (text, number, true/false, date or null) |
| `username` | expression, String | Overrides the connection string's user |
| `password` | expression, String, **secret** | The password (an argument or variable, validation `MYRPA1066`) |

| Type | Output `result` |
|---|---|
| `Db.Query` | The rows: a List of Dictionaries keyed by column (repeated names get `_2`); more than `maxRows` fails with `TooManyItems` |
| `Db.Execute` | The number of rows affected (refused on a read-only connection) |
| `Db.Scalar` | The first column of the first row, or null |

Values come back as workflow values:
- integers become Int; decimal and floating numbers become Decimal;
- dates become DateTime (a date without a zone is read as UTC);
- GUIDs become text, and binary becomes Base64 text.

**SQL injection is impossible by design.** The SQL is a literal text property, so workflow data can never become part
of it. Data goes in only as named parameters, which the driver sends separately from the statement (tests: a name such
as `O'Brien'); DROP TABLE x; --` is stored and found as data).

**Read-only connections:**
- SQLite opens the file read-only.
- PostgreSQL runs the session read-only (`SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY`).
- SQL Server runs every statement in a transaction that is always rolled back.

| errorType | When |
|---|---|
| `ConnectionNotFound` | No connection with that name (the message lists the configured names) |
| `DatabaseConnection` | The database cannot be reached or refused the login (the message never contains the password) |
| `DatabaseError` | The database refused the statement; the message ends with the engine's code, such as `(PostgreSQL 42703)` |
| `ReadOnlyConnection` | A change through a read-only connection |
| `Timeout` | The statement ran longer than `commandTimeoutSeconds` |
| `TooManyItems`, `InvalidInput` | Too many rows; a bad parameter name or a List/Dictionary as a parameter |

## Tests

`tests/MyRPA.Database.Tests` runs the same scenarios on each engine:
- parameters carry data, never SQL;
- values round-trip;
- `maxRows`, engine error codes, read-only enforcement, unknown connections;
- timeouts, and passwords never in messages or logs.

SQLite always runs. SQL Server and PostgreSQL run when `MYRPA_TEST_SQLSERVER` / `MYRPA_TEST_POSTGRES` (connection
strings without the password) and `MYRPA_TEST_SQLSERVER_PASSWORD` / `MYRPA_TEST_POSTGRES_PASSWORD` are set; otherwise
they are reported as skipped, never as passed. For example:

```bash
docker run -d -p 55432:5432 -e POSTGRES_PASSWORD=Pg-Test-Pass-123 postgres:17-alpine
docker run -d -p 14333:1433 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Sql-Test-Pass-123!' mcr.microsoft.com/mssql/server:2022-latest
export MYRPA_TEST_POSTGRES="Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres" MYRPA_TEST_POSTGRES_PASSWORD=Pg-Test-Pass-123
export MYRPA_TEST_SQLSERVER="Server=127.0.0.1,14333;Database=master;User ID=sa;TrustServerCertificate=True" MYRPA_TEST_SQLSERVER_PASSWORD='Sql-Test-Pass-123!'
```

CI runs them in the `Integration services` job.
