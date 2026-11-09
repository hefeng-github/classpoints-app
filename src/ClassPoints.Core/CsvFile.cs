using System.Text;

namespace ClassPoints.Core;

/// <summary>
/// CSV 读取。学校导出的 CSV 十有八九是 GBK，按 UTF-8 读出来全是乱码。
/// 判断顺序不能反过来：GBK 几乎能「读通」任何字节序列，先试 GBK 就永远发现不了自己读错了。
/// </summary>
public static class CsvFile
{
    static CsvFile()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static SheetGrid Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Decode(bytes);
        var rows = ParseCsv(text);
        return new SheetGrid(Path.GetFileName(path), rows);
    }

    public static string Decode(byte[] bytes)
    {
        // 1) BOM 优先
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 2) 严格 UTF-8（解码失败会抛）
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 3) 退回 GB18030
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    public static List<IReadOnlyList<string>> ParseCsv(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        // 去掉尾部全空行
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }
}
