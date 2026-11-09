using System.Globalization;
using System.Text;

namespace ClassPoints.Core;

/// <summary>
/// 姓名规范化。学校成绩单里真正会咬人的是这几种：
/// 「王 芳」两字名中间加空格、全角空格 U+3000、不换行空格 U+00A0、繁体字、零宽字符。
/// 匹配键用 NormalizeKey，展示用 Clean。
/// </summary>
public static class NameNormalizer
{
    // 繁体 → 简体。只收姓名与常见用字，不求全；一对一映射，重复键以最后一次为准（值相同）。
    private const string TradSimpPairs =
        "陳陈張张劉刘黃黄楊杨趙赵吳吴孫孙馬马羅罗鄭郑謝谢許许韓韩鄧邓馮冯蕭萧葉叶蔣蒋蘇苏呂吕盧卢鍾钟譚谭賈贾韋韦鄒邹閻阎龍龙賀贺顧顾龔龚萬万錢钱嚴严齊齐魯鲁歐欧陽阳陰阴陸陆陝陕隨随雙双難难雲云電电頭头顏颜願愿風风飛飞驗验髒脏鬆松鬥斗魚鱼鳥鸟龜龟黨党齡龄華华國国強强軍军偉伟傑杰濤涛鵬鹏輝辉鋒锋東东曉晓靜静麗丽紅红艷艳嬌娇鳳凤蘭兰蓮莲樂乐學学习习書书筆笔畫画話话語语讀读寫写練练題题試试課课師师親亲愛爱歡欢慶庆豐丰進进運运遠远達达過过還还這这們们個个時时間间長长門门問问開开關关閉闭隊队際际險险雜杂雞鸡頂顶項项順顺須须預预領领頻频顆颗顯显飯饭飲饮養养騎骑體体髮发點点齒齿亞亚產产億亿僅仅從从倉仓儀仪價价眾众優优會会傘伞傳传傷伤倫伦偽伪側侧偵侦償偿兒儿兩两冊册凍冻淨净準准涼凉減减幾几憑凭凱凯擊击則则剛刚創创劃划劇剧劍剑劑剂勁劲動动務务勝胜勞劳勢势勵励勸劝匯汇區区協协單单衛卫廠厂廳厅歷历厲厉壓压縣县參参叢丛發发變变號号嘆叹嘗尝嚇吓嗎吗啟启員员響响團团園园圍围圖图圓圆聖圣場场壞坏塊块堅坚壇坛墳坟壯壮聲声殼壳處处備备復复夠够誇夸夾夹奪夺奮奋獎奖妝妆婦妇媽妈嬰婴寧宁寶宝實实寵宠審审憲宪寬宽專专尋寻對对導导將将爾尔層层屬属岡冈島岛崗岗嶺岭巖岩峽峡巒峦幣币帥帅帳帐帶带幫帮幹干庫库廟庙廣广彈弹彌弥彎弯歸归當当錄录彥彦徹彻憶忆憂忧懷怀態态憐怜懇恳應应懲惩戲戏戰战戶户拋抛挾挟捨舍掃扫掛挂採采揀拣換换揮挥損损搖摇搶抢撐撑擺摆攜携攝摄攤摊敗败敘叙敵敌數数斷断晉晋暢畅曠旷條条來来榮荣槍枪樣样標标樞枢樓楼樹树橋桥機机檢检欄栏歲岁殘残殺杀毀毁氣气漢汉沖冲沒没淚泪潔洁灑洒濃浓";

    private static readonly Dictionary<char, char> TradToSimp = BuildMap();

    private static Dictionary<char, char> BuildMap()
    {
        var map = new Dictionary<char, char>(TradSimpPairs.Length / 2);
        for (var i = 0; i + 1 < TradSimpPairs.Length; i += 2)
            map[TradSimpPairs[i]] = TradSimpPairs[i + 1];
        return map;
    }

    /// <summary>用于展示与存储：去首尾空白、把连续空白压成一个空格、全角转半角、繁转简。</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        var lastSpace = false;
        foreach (var ch in s)
        {
            if (IsSpace(ch))
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            lastSpace = false;
            sb.Append(TradToSimp.TryGetValue(ch, out var simp) ? simp : ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>用于匹配：在 Clean 的基础上去掉所有空白与间隔符，避免「王 芳」匹配不上「王芳」。</summary>
    public static string NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (IsSpace(ch)) continue;
            switch (ch)
            {
                case '·':
                case '•':
                case '・':
                case '-':
                case '_':
                case '.':
                case '。':
                case ',':
                case '，':
                case '(':
                case ')':
                case '（':
                case '）':
                    continue;
            }
            sb.Append(TradToSimp.TryGetValue(ch, out var simp) ? simp : ch);
        }
        return sb.ToString().ToUpperInvariant();
    }

    private static bool IsSpace(char ch) =>
        char.IsWhiteSpace(ch) || ch is '\u00A0' or '\u3000' or '\u200B' or '\uFEFF';

    /// <summary>把「王 芳」这类姓名拆回可读形式（仅去掉内部单空格，用于展示预览）。</summary>
    public static string Pretty(string? raw) => Clean(raw);

    /// <summary>
    /// 去掉行首的序号：「1、张三」「03 张三」「12.张三」。
    ///
    /// 老师粘贴名单几乎都是从 Excel 里连序号一起复制的，不去掉就会存成「1、张三」——
    /// 之后按姓名对成绩单就永远对不上，而且这种脏数据事后很难被发现。
    /// 只在前缀后面紧跟分隔符或空白时才去，避免误伤「3班张三」这类真名字。
    /// </summary>
    public static string StripListIndex(string? raw)
    {
        var s = Clean(raw);
        if (s.Length == 0) return s;

        var digits = 0;
        while (digits < s.Length && digits < 4 && char.IsAsciiDigit(s[digits])) digits++;
        if (digits == 0) return s;

        // 数字后面必须有个分隔符（空白或标点），否则它不是序号
        if (digits >= s.Length || !(IsSpace(s[digits]) || IsIndexSeparator(s[digits]))) return s;

        var i = digits;
        while (i < s.Length && (IsSpace(s[i]) || IsIndexSeparator(s[i]))) i++;

        var rest = s[i..].Trim();
        return rest.Length == 0 ? s : rest; // 「3」这种只有序号的，原样留着让人看见
    }

    private static bool IsIndexSeparator(char ch) =>
        ch is '、' or '.' or ',' or ')' or ':' or '-' or '_' or '/' or '\\' or '|' or '　';


    public static bool IsLikelySummaryRow(string? name)
    {
        var k = NormalizeKey(name);
        if (k.Length == 0) return true;
        string[] keys =
        {
            "平均", "班平均", "年级平均", "合计", "总计", "最高", "最低", "排名", "名次",
            "统计", "总分", "人数", "备注", "说明", "序号", "学号",
        };
        return keys.Any(k.Contains) || k.Length > 8;
    }
}
