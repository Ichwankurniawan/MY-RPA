using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Core.Execution;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Spreadsheet.Tests;

/// <summary>The Excel plugin loaded through the real plugin host over a temporary file root, with small limits.</summary>
public sealed class SpreadsheetHost : IAsyncLifetime
{
    public const int MaxRows = 100;

    public const int MaxCells = 1000;

    private static int _counter;

    private readonly string _parent = Directory.CreateTempSubdirectory("myrpa-excel-").FullName;
    private ServiceProvider? _services;

    public SpreadsheetHost()
    {
        FileRoot = Directory.CreateDirectory(Path.Combine(_parent, "root")).FullName;
        Outside = Directory.CreateDirectory(Path.Combine(_parent, "outside")).FullName;
    }

    public PluginSet Plugins { get; private set; } = null!;

    public string FileRoot { get; }

    public string Outside { get; }

    public IServiceProvider Services => _services!;

    public async ValueTask InitializeAsync()
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = SpreadsheetPaths.Plugin };
        source.Settings["fileRoot"] = FileRoot;
        source.Settings["maxRows"] = $"{MaxRows}";
        source.Settings["maxCells"] = $"{MaxCells}";
        options.Sources.Add(source);
        Plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        Assert.False(Plugins.HasRequiredFailures, string.Join(Environment.NewLine, Plugins.Diagnostics));

        _services = new ServiceCollection().AddLogging().AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(Plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await Plugins.DisposeAsync();
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary folder left behind does not change any result.
        }
    }

    public string InRoot(string relative) => Path.Combine(FileRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Runs a Sequence of <paramref name="nodes"/>; <paramref name="inputs"/> are In arguments of type Object.</summary>
    public Task<WorkflowExecutionResult> RunAsync(string nodes, string[]? outputs = null, IReadOnlyDictionary<string, object?>? inputs = null)
    {
        var arguments = string.Join(", ",
            (inputs?.Keys ?? []).Select(i => $$"""{ "name": "{{i}}", "direction": "In", "type": "Object" }""")
                .Concat((outputs ?? []).Select(o => $$"""{ "name": "{{o}}", "direction": "Out", "type": "Object" }""")));
        var json = $$"""
            { "schemaVersion": "1.0", "id": "excel-test", "name": "Excel test", "version": "1.0.0",
              "arguments": [ {{arguments}} ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [ {{nodes}} ] } }
            """;
        var load = Services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return Services.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!, new WorkflowRunRequest { Arguments = inputs ?? new Dictionary<string, object?>() }, TestContext.Current.CancellationToken);
    }

    public static string Node(string type, string properties) =>
        $$"""{ "id": "x{{Interlocked.Increment(ref _counter)}}", "type": "{{type}}", "properties": { {{properties}} } }""";

    public static void AssertSucceeded(WorkflowExecutionResult result) =>
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");

    public void AssertFailed(WorkflowExecutionResult result, string errorType)
    {
        Assert.True(result.Status == ExecutionStatus.Failed, $"Expected Failed/{errorType} but was {result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Equal(errorType, result.Error!.ErrorType);
        Assert.DoesNotContain(FileRoot, result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public static class SpreadsheetPaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(SpreadsheetPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SpreadsheetPluginDirectory").Value!);
}

/// <summary>Writes minimal .xlsx packages from raw SpreadsheetML (no Open XML SDK in tests).</summary>
public static class Xlsx
{
    public const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private const string Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>A workbook with the given sheets (name → sheetData inner XML), optional shared strings and styles.</summary>
    public static void Write(string path, IReadOnlyList<(string Name, string Rows)> sheets, string? sharedStrings = null, string? styles = null)
    {
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var types = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
        var workbookRels = new StringBuilder($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{Relationships}">""");
        var sheetList = new StringBuilder();
        for (var i = 1; i <= sheets.Count; i++)
        {
            types.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
            workbookRels.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i}" Type="{OfficeRelationships}/worksheet" Target="worksheets/sheet{i}.xml"/>""");
            sheetList.Append(CultureInfo.InvariantCulture, $"""<sheet name="{sheets[i - 1].Name}" sheetId="{i}" r:id="rId{i}"/>""");
            Entry(zip, $"xl/worksheets/sheet{i}.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{Main}"><sheetData>{sheets[i - 1].Rows}</sheetData></worksheet>""");
        }

        if (sharedStrings is not null)
        {
            types.Append("""<Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>""");
            workbookRels.Append($"""<Relationship Id="rIdS" Type="{OfficeRelationships}/sharedStrings" Target="sharedStrings.xml"/>""");
            Entry(zip, "xl/sharedStrings.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="{Main}">{sharedStrings}</sst>""");
        }

        if (styles is not null)
        {
            types.Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
            workbookRels.Append($"""<Relationship Id="rIdT" Type="{OfficeRelationships}/styles" Target="styles.xml"/>""");
            Entry(zip, "xl/styles.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="{Main}">{styles}</styleSheet>""");
        }

        Entry(zip, "[Content_Types].xml", types + "</Types>");
        Entry(zip, "_rels/.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{Relationships}"><Relationship Id="rId1" Type="{OfficeRelationships}/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Entry(zip, "xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="{Main}" xmlns:r="{OfficeRelationships}"><sheets>{sheetList}</sheets></workbook>""");
        Entry(zip, "xl/_rels/workbook.xml.rels", workbookRels + "</Relationships>");
    }

    /// <summary>One part of a package as text.</summary>
    public static string ReadPart(string path, string part)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(part)!.Open());
        return reader.ReadToEnd();
    }

    private static void Entry(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}

/// <summary>The errorType values workflows see (ADR-0042), as strings: the tests never compile against the plugin.</summary>
public static class ErrorTypes
{
    public const string FileAccessDenied = "FileAccessDenied";
    public const string FileNotFound = "FileNotFound";
    public const string TooManyItems = "TooManyItems";
    public const string InvalidWorkbook = "InvalidWorkbook";
    public const string SheetNotFound = "SheetNotFound";
    public const string InvalidInput = "InvalidInput";
}
