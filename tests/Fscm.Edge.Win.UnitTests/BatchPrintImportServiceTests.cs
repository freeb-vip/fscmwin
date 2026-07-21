// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO.Compression;
using System.Text;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class BatchPrintImportServiceTests
{
    [Fact]
    public void ReadCsvSortsSequenceAndUsesDefaultCopies()
    {
        string path = Path.Combine(Path.GetTempPath(), $"batch-print-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "sequence,content,copies,remark\r\n2,BOX-002,,默认份数\r\n1,\"BOX,001\",3,包含逗号\r\n", Encoding.UTF8);
            IReadOnlyList<Fscm.Edge.Win.Models.BatchPrintItem> items = BatchPrintImportService.Read(path, 2);
            Assert.Equal(2, items.Count);
            Assert.Equal("BOX,001", items[0].Content);
            Assert.Equal(3, items[0].Copies);
            Assert.Equal("包含逗号", items[0].Remark);
            Assert.Equal("BOX-002", items[1].Content);
            Assert.Equal(2, items[1].Copies);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadXlsxReadsSharedStringsAndNumericCopies()
    {
        string path = Path.Combine(Path.GetTempPath(), $"batch-print-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "xl/workbook.xml", """
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1" /></sheets>
                    </workbook>
                    """);
                WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Target="worksheets/sheet1.xml" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" />
                    </Relationships>
                    """);
                WriteEntry(archive, "xl/sharedStrings.xml", """
                    <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                      <si><t>sequence</t></si><si><t>content</t></si><si><t>copies</t></si><si><t>remark</t></si>
                      <si><t>A001</t></si><si><t>Excel 导入</t></si>
                    </sst>
                    """);
                WriteEntry(archive, "xl/worksheets/sheet1.xml", """
                    <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                      <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="s"><v>3</v></c></row>
                      <row r="2"><c r="A2"><v>1</v></c><c r="B2" t="s"><v>4</v></c><c r="C2"><v>2</v></c><c r="D2" t="s"><v>5</v></c></row>
                    </sheetData></worksheet>
                    """);
            }

            Fscm.Edge.Win.Models.BatchPrintItem item = Assert.Single(BatchPrintImportService.Read(path, 1));
            Assert.Equal("A001", item.Content);
            Assert.Equal(2, item.Copies);
            Assert.Equal("Excel 导入", item.Remark);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadRejectsDuplicateSequence()
    {
        string path = Path.Combine(Path.GetTempPath(), $"batch-print-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "sequence,content,copies\n1,A001,1\n1,A002,1\n", Encoding.UTF8);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => BatchPrintImportService.Read(path, 1));
            Assert.Contains("重复", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadRejectsMoreThanFiveCopies()
    {
        string path = Path.Combine(Path.GetTempPath(), $"batch-print-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "sequence,content,copies\n1,A001,6\n", Encoding.UTF8);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => BatchPrintImportService.Read(path, 1));
            Assert.Contains("5", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using Stream stream = entry.Open();
        using StreamWriter writer = new(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
