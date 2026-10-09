using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Files.FileValues;

namespace MyRPA.Files;

/// <summary><c>Csv.Read</c>: a CSV file as a table.</summary>
public sealed class CsvReadActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Csv.Read"),
        "Read CSV",
        DataFilesCategory,
        "Reads a CSV file (RFC 4180 quoting) into a table: with a header, a List of Dictionaries keyed by column name; without, a List of Lists. Values are text (convert with toInt, toDecimal…). The file is read row by row; fails with TooManyItems above the plugin's maxRows, InvalidCsv for an unclosed quote.",
        [
            PathInput("path", "The CSV file."),
            Input("hasHeader", ActivityValueType.Boolean, "The first row names the columns.", defaultJson: "true"),
            Input("delimiter", ActivityValueType.String, "The column separator (one character; \\t for a tab).", defaultJson: "\",\""),
            EncodingChoice(),
            Output(ActivityValueType.List, "Receives the rows."),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var delimiter = Csv.Delimiter(OptionalText(context, "delimiter"));
        var hasHeader = Flag(context, "hasHeader", true);
        var encoding = TextEncoding(context);
        var full = ReadableFile(options, path);
        var rows = await Io("read", path, async () =>
        {
            var result = new List<object?>();
            List<string>? header = null;
            using var reader = new StreamReader(full, encoding, detectEncodingFromByteOrderMarks: true);
            await foreach (var record in Csv.ReadAsync(reader, delimiter, context.CancellationToken).ConfigureAwait(false))
            {
                if (hasHeader && header is null)
                {
                    header = Csv.Header(record);
                    continue;
                }

                if (result.Count == options.MaxRows)
                {
                    throw Csv.TooMany(path, options.MaxRows);
                }

                result.Add(header is null
                    ? WorkflowValues.List(record)
                    : WorkflowValues.Dictionary(header.Select((name, i) => new KeyValuePair<string, object?>(name, i < record.Count ? record[i] : null))));
            }

            return result;
        }).ConfigureAwait(false);
        SetResult(context, WorkflowValues.List(rows));
        return ActivityResult.Completed;
    }
}

/// <summary><c>Csv.Write</c>: a table to a CSV file.</summary>
public sealed class CsvWriteActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Csv.Write"),
        "Write CSV",
        DataFilesCategory,
        "Writes a table (a List of Dictionaries, or of Lists) to a CSV file. Text that a spreadsheet would run as a formula (starting with = + - @) gets a leading apostrophe unless protectFormulas is false.",
        [
            PathInput("path", "The CSV file."),
            Input("rows", ActivityValueType.List, "The rows.", required: true),
            Input("columns", ActivityValueType.List, "The column names, in order (default: the keys of the rows, in the order first seen)."),
            Input("writeHeader", ActivityValueType.Boolean, "Write the column names as the first row (rows that are Dictionaries).", defaultJson: "true"),
            Input("delimiter", ActivityValueType.String, "The column separator (one character; \\t for a tab).", defaultJson: "\",\""),
            Input("protectFormulas", ActivityValueType.Boolean, "Make text starting with = + - @ inert for spreadsheets.", defaultJson: "true"),
            Overwrite(),
            EncodingChoice(),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var rows = context.Evaluate("rows") is IReadOnlyList<object?> list and not IReadOnlyDictionary<string, object?> ? list : throw Invalid(context, "rows", "a list of rows");
        var delimiter = Csv.Delimiter(OptionalText(context, "delimiter"));
        var protect = Flag(context, "protectFormulas", true);
        var encoding = TextEncoding(context);
        var tables = rows.All(r => r is IReadOnlyDictionary<string, object?>);
        if (!tables && !rows.All(r => r is IReadOnlyList<object?> and not IReadOnlyDictionary<string, object?>))
        {
            throw Invalid(context, "rows", "a list of Dictionaries or a list of Lists");
        }

        List<string> columns = [];
        if (context.HasProperty("columns"))
        {
            columns = context.Evaluate("columns") is IReadOnlyList<object?> names && names.All(n => n is string)
                ? [.. names.Cast<string>()]
                : throw Invalid(context, "columns", "a list of column names");
        }
        else if (tables)
        {
            columns = [.. rows.Cast<IReadOnlyDictionary<string, object?>>().SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal)];
        }

        var writeHeader = tables && Flag(context, "writeHeader", true);
        var full = options.Files.ResolveFileToWrite(path, Flag(context, "overwrite", false));
        string Line(IEnumerable<object?> values) => string.Join(delimiter, values.Select(v => Csv.Field(v, delimiter, protect)));
        await Io("write", path, async () =>
        {
            var writer = new StreamWriter(full, append: false, encoding);
            await using (writer.ConfigureAwait(false))
            {
                if (writeHeader)
                {
                    await writer.WriteAsync(Line(columns) + "\r\n").ConfigureAwait(false);
                }

                foreach (var row in rows)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var values = row is IReadOnlyDictionary<string, object?> d
                        ? columns.Select(c => d.TryGetValue(c, out var v) ? v : null)
                        : (IReadOnlyList<object?>)row!;
                    await writer.WriteAsync(Line(values) + "\r\n").ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>Json.ReadFile</c>: a JSON file as a workflow value.</summary>
public sealed class JsonReadFileActivity(FilesOptions options) : IActivity
{
    /// <summary>Deepest nesting accepted.</summary>
    public const int MaxDepth = 64;

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Json.ReadFile"),
        "Read JSON File",
        DataFilesCategory,
        "Reads a JSON file into a workflow value (objects become Dictionaries, arrays Lists). Fails with InvalidJson for a file that is not JSON, nesting deeper than 64 included.",
        [PathInput("path", "The JSON file."), Output(ActivityValueType.Any, "Receives the value.")])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var full = ReadableFile(options, path);
        var value = await Io("read", path, async () =>
        {
            var stream = File.OpenRead(full);
            await using (stream.ConfigureAwait(false))
            {
                JsonDocument document;
                try
                {
                    document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = MaxDepth }, context.CancellationToken).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new ActivityFailedException("InvalidJson", $"'{path}' is not valid JSON (line {ex.LineNumber + 1}).", ex);
                }

                using (document)
                {
                    return WorkflowValues.TryFromJsonUntyped(document.RootElement, out var parsed, out var error)
                        ? parsed
                        : throw new ActivityFailedException("InvalidJson", $"The JSON in '{path}' cannot become a workflow value: {error}");
                }
            }
        }).ConfigureAwait(false);
        SetResult(context, value);
        return ActivityResult.Completed;
    }
}

/// <summary><c>Json.WriteFile</c>: a workflow value to a JSON file.</summary>
public sealed class JsonWriteFileActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Json.WriteFile"),
        "Write JSON File",
        DataFilesCategory,
        "Writes a workflow value to a JSON file (UTF-8).",
        [
            PathInput("path", "The JSON file."),
            Input("value", ActivityValueType.Any, "The value.", required: true),
            Input("indented", ActivityValueType.Boolean, "One value per line, indented.", defaultJson: "true"),
            Overwrite(),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var value = context.Evaluate("value");
        var indented = Flag(context, "indented", true);
        var full = options.Files.ResolveFileToWrite(path, Flag(context, "overwrite", false));
        await Io("write", path, async () =>
        {
            var stream = File.Create(full);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                await using (writer.ConfigureAwait(false))
                {
                    WorkflowValues.WriteJson(writer, value);
                    await writer.FlushAsync(context.CancellationToken).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>Xml.ReadFile</c>: an XML file as nested Dictionaries.</summary>
public sealed class XmlReadFileActivity(FilesOptions options) : IActivity
{
    /// <summary>Deepest element nesting accepted.</summary>
    public const int MaxDepth = 64;

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Xml.ReadFile"),
        "Read XML File",
        DataFilesCategory,
        "Reads an XML file into a Dictionary per element: { name, attributes (a Dictionary), text (null when none), children (a List) }. Document type definitions and external entities are refused (InvalidXml), as is nesting deeper than 64.",
        [PathInput("path", "The XML file."), Output(ActivityValueType.Dictionary, "Receives the root element.")])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var full = ReadableFile(options, path);
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };
        var root = await Io("read", path, async () =>
        {
            var stream = File.OpenRead(full);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = XmlReader.Create(stream, settings);
                try
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            return await ElementAsync(reader, 1, context.CancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (XmlException ex)
                {
                    throw new ActivityFailedException("InvalidXml", $"'{path}' is not XML this activity reads (line {ex.LineNumber}); document type definitions and external entities are not allowed.", ex);
                }

                throw new ActivityFailedException("InvalidXml", $"'{path}' has no root element.");
            }
        }).ConfigureAwait(false);
        SetResult(context, root);
        return ActivityResult.Completed;
    }

    private static async Task<IReadOnlyDictionary<string, object?>> ElementAsync(XmlReader reader, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
        {
            throw new ActivityFailedException("InvalidXml", $"The XML is nested deeper than {MaxDepth} elements.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var name = reader.Name;
        var attributes = new List<KeyValuePair<string, object?>>();
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                attributes.Add(new(reader.Name, reader.Value));
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        var children = new List<object?>();
        var text = new StringBuilder();
        var hasText = false;
        if (!reader.IsEmptyElement)
        {
            while (await reader.ReadAsync().ConfigureAwait(false) && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    children.Add(await ElementAsync(reader, depth + 1, cancellationToken).ConfigureAwait(false));
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                {
                    text.Append(reader.Value);
                    hasText = true;
                }
            }
        }

        return WorkflowValues.Dictionary(
        [
            new("name", name),
            new("attributes", WorkflowValues.Dictionary(attributes)),
            new("text", hasText ? text.ToString() : null),
            new("children", WorkflowValues.List(children)),
        ]);
    }
}
