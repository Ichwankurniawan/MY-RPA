using System.Globalization;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace MyRPA.Documents;

/// <summary><c>Pdf.ReadText</c>: the text of a PDF's pages, in reading order (ADR-0043).</summary>
public sealed class PdfReadTextActivity(DocumentsOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Pdf.ReadText"),
        "Read PDF Text",
        Pdfs.Category,
        "Reads the text of a PDF (all pages, or pages such as '1-3, 5') in reading order, for example to extract an invoice number with Match Text. A scanned PDF holds images, not text, and gives empty text. Fails with InvalidDocument for a damaged file, EncryptedDocument without the right password, or TooManyItems above the plugin's maxPages or maxTextChars.",
        [
            Pdfs.PathInput(),
            Pdfs.Input("pages", ActivityValueType.String, "The pages to read, such as '1-3, 5' or '2-' (default: all)."),
            Pdfs.Password(),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the text, pages separated by a blank line.") { ValueType = ActivityValueType.String },
            new("pageTexts", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the text of each page read (a List of String).") { ValueType = ActivityValueType.List },
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Pdfs.Text(context, "path");
        var full = Pdfs.ReadableFile(options, path);
        var pagesText = context.HasProperty("pages") ? Pdfs.Text(context, "pages") : null;
        var texts = Pdfs.Use(path, () =>
        {
            using var document = Pdfs.Open(full, context);
            var pages = PageList(pagesText, document.NumberOfPages, context);
            if (pages.Count > options.MaxPages)
            {
                throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"'{path}' has more than {options.MaxPages} pages to read (setting maxPages); give fewer pages.");
            }

            var result = new List<object?>();
            long characters = 0;
            foreach (var number in pages)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(document.GetPage(number));
                characters += text.Length;
                if (characters > options.MaxTextChars)
                {
                    throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"'{path}' has more than {options.MaxTextChars} characters of text (setting maxTextChars); give fewer pages.");
                }

                result.Add(text);
            }

            return result;
        });

        context.SetValue(context.GetName("result"), string.Join("\n\n", texts.Cast<string>()));
        if (context.HasProperty("pageTexts"))
        {
            context.SetValue(context.GetName("pageTexts"), WorkflowValues.List(texts));
        }

        return ActivityResult.CompletedTask;
    }

    /// <summary>The page numbers of a selection such as "1-3, 5, 8-" (in the order given, without repeats).</summary>
    internal static List<int> PageList(string? selection, int count, IActivityContext context)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return [.. Enumerable.Range(1, count)];
        }

        var pages = new List<int>();
        var seen = new HashSet<int>();
        foreach (var part in selection.Split(',', StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2 || !int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first < 1)
            {
                throw Pdfs.Invalid(context, "pages", "pages such as '1-3, 5' or '2-'");
            }

            var last = first;
            if (bounds.Length == 2)
            {
                last = bounds[1].Length == 0 ? count
                    : int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end) && end >= first ? end
                    : throw Pdfs.Invalid(context, "pages", "pages such as '1-3, 5' or '2-'");
            }

            if (last > count)
            {
                throw new ActivityFailedException(Pdfs.InvalidInput, $"'pages' of {context.Node.Type} '{context.Node.Id}' asks for page {last}; the document has {count}.");
            }

            for (var page = first; page <= last; page++)
            {
                if (seen.Add(page))
                {
                    pages.Add(page);
                }
            }
        }

        return pages;
    }
}

/// <summary><c>Pdf.GetInfo</c>: a PDF's page count and document properties (ADR-0043).</summary>
public sealed class PdfGetInfoActivity(DocumentsOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Pdf.GetInfo"),
        "Get PDF Info",
        Pdfs.Category,
        "A PDF's page count and properties as a Dictionary: pages, title, author, subject, keywords, creator, producer, created, modified (null when missing), encrypted and version.",
        [
            Pdfs.PathInput(),
            Pdfs.Password(),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the information (a Dictionary).") { ValueType = ActivityValueType.Dictionary },
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Pdfs.Text(context, "path");
        var full = Pdfs.ReadableFile(options, path);
        var info = Pdfs.Use(path, () =>
        {
            using var document = Pdfs.Open(full, context);
            var information = document.Information;
            static object? Text(string? value) => string.IsNullOrEmpty(value) ? null : value;
            return WorkflowValues.Dictionary(
            [
                new("pages", (long)document.NumberOfPages),
                new("title", Text(information.Title)),
                new("author", Text(information.Author)),
                new("subject", Text(information.Subject)),
                new("keywords", Text(information.Keywords)),
                new("creator", Text(information.Creator)),
                new("producer", Text(information.Producer)),
                new("created", information.GetCreatedDateTimeOffset()),
                new("modified", information.GetModifiedDateTimeOffset()),
                new("encrypted", document.IsEncrypted),
                new("version", document.Version.ToString("0.0", CultureInfo.InvariantCulture)),
            ]);
        });

        context.SetValue(context.GetName("result"), info);
        return ActivityResult.CompletedTask;
    }
}

/// <summary>Property declarations, opening and failure classification shared by the PDF activities.</summary>
internal static class Pdfs
{
    public const string Category = "Documents";

    public const string InvalidInput = "InvalidInput";

    /// <summary>The file is not a PDF this plugin reads (damaged, truncated or not a PDF).</summary>
    public const string InvalidDocument = "InvalidDocument";

    /// <summary>The PDF is encrypted and no password, or a wrong one, was given.</summary>
    public const string EncryptedDocument = "EncryptedDocument";

    public static ActivityPropertyDefinition Input(string name, ActivityValueType type, string description, bool required = false) =>
        new(name, ActivityPropertyKind.Expression, required, description) { ValueType = type };

    public static ActivityPropertyDefinition PathInput() =>
        Input("path", ActivityValueType.String, "The PDF file, relative to the plugin's file root (or absolute inside it).", required: true);

    public static ActivityPropertyDefinition Password() =>
        new("password", ActivityPropertyKind.Expression, isRequired: false, "The password of an encrypted PDF: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true };

    public static string Text(IActivityContext context, string name) =>
        context.Evaluate(name) is string text ? text : throw Invalid(context, name, "text");

    public static ActivityFailedException Invalid(IActivityContext context, string name, string expected) =>
        new(InvalidInput, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");

    /// <summary>An existing file inside the root, not larger than the limit.</summary>
    public static string ReadableFile(DocumentsOptions options, string path)
    {
        var full = options.Files.ResolveExistingFile(path);
        return new FileInfo(full).Length <= options.MaxFileBytes
            ? full
            : throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"The file '{path}' is larger than the limit of {options.MaxFileBytes} bytes (setting maxFileBytes).");
    }

    /// <summary>Opens the PDF; the password (a secret) goes only to the parser.</summary>
    public static PdfDocument Open(string full, IActivityContext context)
    {
        var parsing = new ParsingOptions { SkipMissingFonts = true };
        if (context.HasProperty("password") && context.Evaluate("password") is { } value)
        {
            parsing.Password = value as string ?? throw Invalid(context, "password", "text");
        }

        return PdfDocument.Open(full, parsing);
    }

    /// <summary>
    /// Runs PDF work. A PDF is untrusted input parsed by a third-party library, so any parser failure is classified as
    /// InvalidDocument (the cause stays the inner exception); messages name the path as given, never the password.
    /// </summary>
    public static T Use<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new ActivityFailedException(EncryptedDocument, $"'{path}' is encrypted; give its password (or the right one) in 'password'.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActivityFailedException(FileErrorTypes.FileIoError, $"Cannot read '{path}': it is in use, read-only or not accessible.", ex);
        }
        // A damaged or hostile PDF can make the parser throw almost anything; cancellation, our own failures and
        // out-of-memory still pass through.
        catch (Exception ex) when (ex is not (OperationCanceledException or ActivityFailedException or OutOfMemoryException))
        {
            throw new ActivityFailedException(InvalidDocument, $"'{path}' is not a PDF this activity can read (it may be damaged).", ex);
        }
    }
}
