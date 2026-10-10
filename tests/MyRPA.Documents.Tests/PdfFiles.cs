using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyRPA.Documents.Tests;

/// <summary>
/// Writes small, valid PDF files by hand (PdfPig belongs to the plugin only): one page per text, lines in Helvetica, an
/// optional document information dictionary, and optional RC4 encryption with a user password (the PDF standard
/// security handler, revision 2), so password handling is tested against real encrypted content.
/// </summary>
public static class PdfFiles
{
    private static readonly byte[] _padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>A PDF whose page <c>i</c> shows the lines of <paramref name="pages"/>[i].</summary>
    public static byte[] Build(IReadOnlyList<string> pages, IReadOnlyDictionary<string, string>? info = null, string? userPassword = null)
    {
        // Object bodies are produced when written, so the page tree sees every page and encryption every object number.
        var objects = new List<Func<Encryption?, byte[]>>();
        int Add(Func<Encryption?, byte[]> body)
        {
            objects.Add(body);
            return objects.Count;
        }

        var pageIds = new List<int>();
        var catalog = Add(_ => Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        var pageTree = Add(_ => Ascii($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => $"{p} 0 R"))}] /Count {pageIds.Count} >>"));
        var font = Add(_ => Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        int? infoId = null;
        if (info is not null)
        {
            var id = objects.Count + 1;
            infoId = Add(e => Ascii("<< " + string.Concat(info.Select(kv => $"/{kv.Key} {PdfString(kv.Value, e, id)} ")) + ">>"));
        }

        foreach (var text in pages)
        {
            var contentId = objects.Count + 2;
            pageIds.Add(Add(_ => Ascii($"<< /Type /Page /Parent {pageTree} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {contentId} 0 R >>")));
            var content = new StringBuilder("BT\n/F1 12 Tf\n72 720 Td\n");
            foreach (var line in text.Split('\n'))
            {
                content.Append('(').Append(Escape(line)).Append(") Tj\n0 -16 Td\n");
            }

            content.Append("ET");
            var stream = Encoding.Latin1.GetBytes(content.ToString());
            Add(e =>
            {
                var data = e is null ? stream : e.Apply(contentId, stream);
                return [.. Ascii($"<< /Length {data.Length} >>\nstream\n"), .. data, .. Ascii("\nendstream")];
            });
        }

        Encryption? encryption = null;
        var fileId = MD5Of(Encoding.ASCII.GetBytes("myrpa-test-" + pages.Count.ToString(CultureInfo.InvariantCulture)));
        int? encryptId = null;
        if (userPassword is not null)
        {
            var keys = new Encryption(userPassword, "owner-" + userPassword, fileId);
            encryption = keys;
            encryptId = Add(_ => Ascii($"<< /Filter /Standard /V 1 /R 2 /Length 40 /P {Encryption.Permissions} /O <{Convert.ToHexString(keys.Owner)}> /U <{Convert.ToHexString(keys.User)}> >>"));
        }

        using var output = new MemoryStream();
        output.Write(Ascii("%PDF-1.4\n"));
        output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            var number = i + 1;
            output.Write(Ascii($"{number} 0 obj\n"));
            output.Write(objects[i](number == encryptId ? null : encryption));
            output.Write(Ascii("\nendobj\n"));
        }

        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        var hexId = Convert.ToHexString(fileId);
        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /ID [<{hexId}> <{hexId}>]");
        if (infoId is not null)
        {
            table.Append(CultureInfo.InvariantCulture, $" /Info {infoId} 0 R");
        }

        if (encryptId is not null)
        {
            table.Append(CultureInfo.InvariantCulture, $" /Encrypt {encryptId} 0 R");
        }

        table.Append(CultureInfo.InvariantCulture, $" >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Ascii(table.ToString()));
        return output.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    private static string PdfString(string value, Encryption? encryption, int objectNumber) =>
        encryption is null ? $"({Escape(value)})" : $"<{Convert.ToHexString(encryption.Apply(objectNumber, Encoding.Latin1.GetBytes(value)))}>";

    // MD5 and RC4 are what the PDF standard security handler (revision 2) prescribes; this writes test files, it protects nothing.
#pragma warning disable CA5351
    private static byte[] MD5Of(byte[] data) => MD5.HashData(data);
#pragma warning restore CA5351

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var result = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            result[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return result;
    }

    private static byte[] Pad(string password) => [.. Encoding.Latin1.GetBytes(password).Concat(_padding).Take(32)];

    /// <summary>The 40-bit RC4 keys of the standard security handler, revision 2 (PDF 1.7, algorithms 3.1 to 3.4).</summary>
    private sealed class Encryption
    {
        public const int Permissions = -44;

        private readonly byte[] _key;

        public Encryption(string userPassword, string ownerPassword, byte[] fileId)
        {
            Owner = Rc4(MD5Of(Pad(ownerPassword))[..5], Pad(userPassword));
            _key = MD5Of([.. Pad(userPassword), .. Owner, .. BitConverter.GetBytes(Permissions), .. fileId])[..5];
            User = Rc4(_key, _padding);
        }

        public byte[] Owner { get; }

        public byte[] User { get; }

        /// <summary>Encrypts (or decrypts) a string or stream of one object.</summary>
        public byte[] Apply(int objectNumber, byte[] data)
        {
            var objectKey = MD5Of([.. _key, (byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16), 0, 0])[..10];
            return Rc4(objectKey, data);
        }
    }
}
