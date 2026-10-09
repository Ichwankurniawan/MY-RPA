using System.Runtime.CompilerServices;
using System.Text;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Files;

/// <summary>RFC 4180 CSV: quoted fields, doubled quotes, line breaks inside quotes; one character as delimiter.</summary>
internal static class Csv
{
    /// <summary>Reads records one at a time (the file is never held whole). Blank lines are skipped.</summary>
    public static async IAsyncEnumerable<List<string>> ReadAsync(TextReader reader, char delimiter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        var quoted = false;
        var quoteInQuoted = false; // a quote inside a quoted field: a doubled quote or the field's end
        var fieldStarted = false;
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (quoteInQuoted)
                {
                    quoteInQuoted = false;
                    if (c == '"')
                    {
                        field.Append('"');
                        continue;
                    }

                    quoted = false;
                }

                if (quoted)
                {
                    if (c == '"')
                    {
                        quoteInQuoted = true;
                    }
                    else
                    {
                        field.Append(c);
                    }

                    continue;
                }

                if (c == '"' && !fieldStarted)
                {
                    quoted = true;
                    fieldStarted = true;
                }
                else if (c == delimiter)
                {
                    record.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                }
                else if (c == '\n')
                {
                    if (record.Count > 0 || fieldStarted)
                    {
                        record.Add(field.ToString());
                        yield return record;
                        record = [];
                    }

                    field.Clear();
                    fieldStarted = false;
                }
                else if (c != '\r')
                {
                    field.Append(c);
                    fieldStarted = true;
                }
            }
        }

        if (quoted && !quoteInQuoted)
        {
            throw new ActivityFailedException("InvalidCsv", "The CSV ends inside a quoted field (a quote is not closed).");
        }

        if (record.Count > 0 || fieldStarted)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    /// <summary>One CSV field: quoted when needed; text that a spreadsheet would run as a formula is made inert.</summary>
    public static string Field(object? value, char delimiter, bool protectFormulas)
    {
        var text = value is null ? string.Empty : WorkflowValues.ToDisplayString(value);
        if (protectFormulas && value is string && text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        var quote = text.Contains(delimiter) || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            || (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])));
        return quote ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }

    /// <summary>The delimiter character; <c>\t</c> (backslash t) or a real tab means a tab.</summary>
    public static char Delimiter(string? text) => text switch
    {
        null => ',',
        "\\t" => '\t',
        { Length: 1 } when text[0] is not '"' and not '\r' and not '\n' => text[0],
        _ => throw new ActivityFailedException("InvalidInput", "'delimiter' must be one character (not a quote or line break); \\t means a tab."),
    };

    /// <summary>Header names made unique (a blank or repeated name becomes column{n}).</summary>
    public static List<string> Header(List<string> names)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i].Trim();
            if (name.Length == 0 || !used.Add(name))
            {
                name = $"column{i + 1}";
                used.Add(name);
            }

            result.Add(name);
        }

        return result;
    }

    public static ActivityFailedException TooMany(string path, int max) =>
        new(FileErrorTypes.TooManyItems, $"'{path}' has more than {max} rows (setting maxRows).");
}
