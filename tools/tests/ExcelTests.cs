using ClassPoints.Core;
using ClassPoints.Data;
using NPOI.HSSF.UserModel;

namespace ClassPoints.Tests;

/// <summary>
/// Excel 读写的往返测试。
///
/// 补这组用例的直接原因：NPOI 从 2.7.2 升到 2.8.1（顺手换掉了有已知漏洞的 ImageSharp，
/// 改用 SkiaSharp）是一次主版本跳跃，而 Excel 导入是老师把名单和成绩弄进来**唯一**的批量入口。
/// 升版之前这条链路上一个测例都没有 —— 真坏了要等开学才发现。
///
/// 覆盖四件会真出事的事：
/// ① 写进去的中文和数字能原样读回来（字符编码、DataFormatter 的格式化规则都会在这里翻车）；
/// ② .xls 和 .xlsx 都能读（老师手里的老文件经常是 .xls，"一把梭"不能只是注释里说说）；
/// ③ 日期不能被读成 45000 这种序列号；
/// ④ 公式要按算出来的值取，不能把 "=1+2" 当文本塞进界面。
/// </summary>
internal static class ExcelTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "classpoints-excel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            RunXlsx(dir, Check);
            RunXls(dir, Check);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* 临时目录删不掉不影响结论 */ }
        }
    }

    // ---------------- .xlsx：ClosedXML 写 → NPOI 读 ----------------
    private static void RunXlsx(string dir, Action<string, bool, string> check)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);

        var path = Path.Combine(dir, "名单.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var s1 = wb.Worksheets.Add("名单");
            s1.Cell(1, 1).Value = "姓名";
            s1.Cell(1, 2).Value = "学号";
            s1.Cell(2, 1).Value = "张三";
            s1.Cell(2, 2).Value = "01";
            s1.Cell(3, 1).Value = "李四";      // 学号留空：转学来的还没编号
            s1.Cell(4, 1).Value = "王五";
            s1.Cell(4, 2).Value = "03";
            s1.Cell(4, 3).Value = new DateTime(2026, 11, 9);
            s1.Cell(4, 3).Style.DateFormat.Format = "yyyy-mm-dd";
            s1.Cell(5, 1).Value = "赵六";
            s1.Cell(5, 2).FormulaA1 = "=1+2";

            // 数值列：班里真正的分数都是这种单元格。NPOI 2.8 起格式化数值要靠 SkiaSharp，
            // 少了那个 DLL 会静默返回空串 —— 这一行就是专门盯它的。
            s1.Cell(1, 4).Value = "积分";
            s1.Cell(2, 4).Value = 88.5;
            s1.Cell(3, 4).Value = 92;
            s1.Cell(4, 4).Value = -3;

            var s2 = wb.Worksheets.Add("成绩");
            s2.Cell(1, 1).Value = "期中";
            wb.SaveAs(path);
        }

        var names = ExcelReader.SheetNames(path);
        Check("xlsx 工作表名与顺序都对", names.Count == 2 && names[0] == "名单" && names[1] == "成绩",
            string.Join(",", names));

        var grid = ExcelReader.Read(path);
        Check("xlsx 读第一张表", grid.Name == "名单", grid.Name);
        Check("xlsx 中文表头没乱码", grid.Cell(0, 0) == "姓名" && grid.Cell(0, 1) == "学号",
            grid.Cell(0, 0) + "/" + grid.Cell(0, 1));
        Check("xlsx 中文姓名没乱码", grid.Cell(1, 0) == "张三" && grid.Cell(3, 0) == "王五",
            grid.Cell(1, 0) + "/" + grid.Cell(3, 0));
        Check("xlsx 学号 01 不被吃成 1", grid.Cell(1, 1) == "01", grid.Cell(1, 1));
        Check("xlsx 空单元格读成空串", grid.Cell(2, 1) == "", "[" + grid.Cell(2, 1) + "]");
        Check("xlsx 日期读成 yyyy-MM-dd 而不是序列号", grid.Cell(3, 2) == "2026-11-09", grid.Cell(3, 2));
        Check("xlsx 公式按计算结果取", grid.Cell(4, 1) == "3", grid.Cell(4, 1));
        Check("xlsx 小数不被丢掉", grid.Cell(1, 3) == "88.5", "[" + grid.Cell(1, 3) + "]");
        Check("xlsx 整数不带小数位", grid.Cell(2, 3) == "92", "[" + grid.Cell(2, 3) + "]");
        Check("xlsx 负分保留负号", grid.Cell(3, 3) == "-3", "[" + grid.Cell(3, 3) + "]");
        Check("xlsx 行数正确（5 行）", grid.RowCount == 5, grid.RowCount.ToString());
        Check("xlsx 列数正确（4 列）", grid.ColumnCount == 4, grid.ColumnCount.ToString());

        var second = ExcelReader.Read(path, 1);
        Check("xlsx 能指定读第二张表", second.Name == "成绩" && second.Cell(0, 0) == "期中",
            second.Name + "/" + second.Cell(0, 0));

        // 越界的 sheetIndex 要夹回最后一张，而不是抛异常（界面上的下拉可能还没刷新）
        var clamped = ExcelReader.Read(path, 99);
        Check("xlsx 工作表序号越界时夹到最后一张", clamped.Name == "成绩", clamped.Name);
    }

    // ---------------- .xls：NPOI 自己写 → 自己的读回 ----------------
    private static void RunXls(string dir, Action<string, bool, string> check)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);

        var path = Path.Combine(dir, "老成绩单.xls");
        using (var wb = new HSSFWorkbook())
        {
            var sheet = wb.CreateSheet("总分");
            var head = sheet.CreateRow(0);
            head.CreateCell(0).SetCellValue("姓名");
            head.CreateCell(1).SetCellValue("总分");

            var r1 = sheet.CreateRow(1);
            r1.CreateCell(0).SetCellValue("张三");
            r1.CreateCell(1).SetCellValue(88.5);

            var r2 = sheet.CreateRow(2);
            r2.CreateCell(0).SetCellValue("李四");
            r2.CreateCell(1).SetCellValue(92);

            // 尾部空行：老师拖过格式之后很常见，读的时候必须去掉，否则表头扫描会飘
            sheet.CreateRow(3);
            sheet.CreateRow(4);

            using var fs = File.Create(path);
            wb.Write(fs);
        }

        var grid = ExcelReader.Read(path);
        Check(".xls 能读（HSSF 老格式）", grid.Name == "总分", grid.Name);
        Check(".xls 中文没乱码", grid.Cell(0, 0) == "姓名" && grid.Cell(1, 0) == "张三",
            grid.Cell(0, 0) + "/" + grid.Cell(1, 0));
        Check(".xls 小数分保留一位", grid.Cell(1, 1) == "88.5", grid.Cell(1, 1));
        Check(".xls 整数分不补小数位", grid.Cell(2, 1) == "92", grid.Cell(2, 1));
        Check(".xls 尾部空行被去掉", grid.RowCount == 3, grid.RowCount.ToString());
    }
}
