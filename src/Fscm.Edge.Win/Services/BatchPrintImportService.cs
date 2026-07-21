// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

public static class BatchPrintImportService
{
    private static readonly string[] RequiredHeaders = ["sequence", "content", "copies"];

    public static IReadOnlyList<BatchPrintItem> Read(string path, int defaultCopies)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        IReadOnlyList<IReadOnlyList<string>> rows = extension switch
        {
            ".csv" => ReadCsv(path),
            ".xlsx" => ReadXlsx(path),
            _ => throw new InvalidDataException("仅支持 .csv 和 .xlsx 文件。"),
        };
        PrintJobDispatchPolicy.EnsureCopiesAllowed(defaultCopies);
        return ConvertRows(rows, defaultCopies);
    }

    public static string CsvTemplate => "sequence,content,copies,remark\r\n1,H01-00-01,1,\r\n2,H01-00-02,1,\r\n3,H01-00-03,2,打印两份\r\n";

    private static IReadOnlyList<BatchPrintItem> ConvertRows(IReadOnlyList<IReadOnlyList<string>> rows, int defaultCopies)
    {
        if (rows.Count == 0)
        {
            throw new InvalidDataException("导入文件没有数据。");
        }

        var header = rows[0]
            .Select((value, index) => new { Name = value.Trim().TrimStart('\uFEFF').ToLowerInvariant(), Index = index })
            .Where(value => value.Name.Length > 0)
            .GroupBy(value => value.Name)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
        foreach (string required in RequiredHeaders)
        {
            if (!header.ContainsKey(required))
            {
                throw new InvalidDataException($"导入文件缺少字段：{required}。");
            }
        }

        var items = new List<(int Sequence, BatchPrintItem Item)>();
        var sequences = new HashSet<int>();
        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            IReadOnlyList<string> row = rows[rowIndex];
            string sequenceText = Cell(row, header["sequence"]).Trim();
            string content = Cell(row, header["content"]).Trim();
            string copiesText = Cell(row, header["copies"]).Trim();
            string remark = header.TryGetValue("remark", out int remarkIndex) ? Cell(row, remarkIndex).Trim() : string.Empty;
            if (sequenceText.Length == 0 && content.Length == 0 && copiesText.Length == 0 && remark.Length == 0)
            {
                continue;
            }
            if (!int.TryParse(sequenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sequence) || sequence < 1)
            {
                throw new InvalidDataException($"第 {rowIndex + 1} 行 sequence 必须是正整数。");
            }
            if (!sequences.Add(sequence))
            {
                throw new InvalidDataException($"第 {rowIndex + 1} 行 sequence {sequence} 重复。");
            }
            if (content.Length == 0)
            {
                throw new InvalidDataException($"第 {rowIndex + 1} 行 content 不能为空。");
            }
            int copies = defaultCopies;
            if (copiesText.Length > 0 && (!int.TryParse(copiesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out copies) || !PrintJobDispatchPolicy.IsCopiesAllowed(copies)))
            {
                throw new InvalidDataException($"第 {rowIndex + 1} 行 copies 必须在 1 到 {PrintJobDispatchPolicy.MaxCopiesPerContent} 之间。");
            }
            items.Add((sequence, new BatchPrintItem { Content = content, Copies = copies, Remark = remark, Source = "文件导入" }));
        }
        if (items.Count is < 1 or > 1000)
        {
            throw new InvalidDataException("每个批次必须包含 1 到 1000 条内容。");
        }
        return items.OrderBy(value => value.Sequence)
            .Select((value, index) =>
            {
                value.Item.SequenceNo = index + 1;
                return value.Item;
            })
            .ToList();
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadCsv(string path)
    {
        string content = File.ReadAllText(path, Encoding.UTF8);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < content.Length; index++)
        {
            char current = content[index];
            if (current == '"')
            {
                if (quoted && index + 1 < content.Length && content[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (current == ',' && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((current == '\r' || current == '\n') && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
                if (current == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                field.Append(current);
            }
        }
        if (quoted)
        {
            throw new InvalidDataException("CSV 文件包含未闭合的引号。");
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadXlsx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sharedStrings = ReadSharedStrings(archive, spreadsheet);
        XDocument workbook = ReadXml(archive, "xl/workbook.xml");
        string relationshipId = workbook.Descendants(spreadsheet + "sheet").FirstOrDefault()?.Attribute(relationships + "id")?.Value
            ?? throw new InvalidDataException("Excel 文件没有工作表。");
        XDocument workbookRelationships = ReadXml(archive, "xl/_rels/workbook.xml.rels");
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        string target = workbookRelationships.Descendants(packageRelationships + "Relationship")
            .FirstOrDefault(value => value.Attribute("Id")?.Value == relationshipId)?.Attribute("Target")?.Value
            ?? throw new InvalidDataException("无法定位 Excel 第一张工作表。");
        string sheetPath = NormalizeZipPath(target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target);
        XDocument sheet = ReadXml(archive, sheetPath);
        var rows = new List<IReadOnlyList<string>>();
        foreach (XElement rowElement in sheet.Descendants(spreadsheet + "row"))
        {
            var values = new SortedDictionary<int, string>();
            foreach (XElement cell in rowElement.Elements(spreadsheet + "c"))
            {
                int column = ColumnIndex(cell.Attribute("r")?.Value);
                string type = cell.Attribute("t")?.Value ?? string.Empty;
                string raw = cell.Element(spreadsheet + "v")?.Value ?? string.Empty;
                string value = type switch
                {
                    "s" when int.TryParse(raw, out int sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count => sharedStrings[sharedIndex],
                    "inlineStr" => string.Concat(cell.Descendants(spreadsheet + "t").Select(text => text.Value)),
                    _ => raw,
                };
                values[column] = value;
            }
            if (values.Count == 0)
            {
                rows.Add([]);
                continue;
            }
            var row = Enumerable.Repeat(string.Empty, values.Keys.Max() + 1).ToList();
            foreach ((int column, string value) in values)
            {
                row[column] = value;
            }
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive, XNamespace spreadsheet)
    {
        ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }
        using Stream stream = entry.Open();
        XDocument document = XDocument.Load(stream);
        return document.Descendants(spreadsheet + "si")
            .Select(item => string.Concat(item.Descendants(spreadsheet + "t").Select(text => text.Value)))
            .ToList();
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path) ?? throw new InvalidDataException($"Excel 文件缺少 {path}。");
        using Stream stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string NormalizeZipPath(string path)
    {
        var segments = new List<string>();
        foreach (string segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }
        return string.Join('/', segments);
    }

    private static int ColumnIndex(string? cellReference)
    {
        int value = 0;
        foreach (char character in cellReference ?? string.Empty)
        {
            if (!char.IsLetter(character))
            {
                break;
            }
            value = (value * 26) + char.ToUpperInvariant(character) - 'A' + 1;
        }
        return Math.Max(0, value - 1);
    }

    private static string Cell(IReadOnlyList<string> row, int index)
    {
        return index >= 0 && index < row.Count ? row[index] : string.Empty;
    }
}
