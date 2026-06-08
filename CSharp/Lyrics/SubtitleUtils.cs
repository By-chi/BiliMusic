using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

public static class SubtitleUtils
{
    private static Dictionary<string, string> _t2sDict;
    private static readonly object _dictLock = new();

    public static void LoadTSDictionary()
    {
        if (_t2sDict != null) return;
        lock (_dictLock)
        {
            if (_t2sDict != null) return;
            _t2sDict = new Dictionary<string, string>(6000);
            string path = ProjectSettings.GlobalizePath("res://Data/TSCharacters.txt");
            try
            {
                using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                if (file == null)
                {
                    GD.PrintErr($"[TSDictionary] 无法打开词典文件: {path}");
                    return;
                }
                while (!file.EofReached())
                {
                    string line = file.GetLine().Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2 && parts[0].Length == 1 && parts[1].Length > 0)
                    {
                        string simplified = parts[1].Split(' ')[0];
                        _t2sDict[parts[0]] = simplified;
                    }
                }
                GD.Print($"[TSDictionary] 加载完成，共 {_t2sDict.Count} 条映射");
            }
            catch (Exception e) { GD.PrintErr($"[TSDictionary] 加载失败: {e.Message}"); }
        }
    }

    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (_t2sDict == null) LoadTSDictionary();
        if (_t2sDict == null || _t2sDict.Count == 0) return text;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (_t2sDict.TryGetValue(c.ToString(), out string simplified))
                sb.Append(simplified);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static double CalculateSimilarity(string a, string b)
    {
        a = ToSimplified(a).Replace(" ", "").Replace("\n", "");
        b = ToSimplified(b).Replace(" ", "").Replace("\n", "");
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;

        var setA = new HashSet<string>();
        var setB = new HashSet<string>();
        for (int i = 0; i < a.Length - 1; i++) setA.Add(a.Substring(i, 2));
        for (int i = 0; i < b.Length - 1; i++) setB.Add(b.Substring(i, 2));

        int inter = setA.Intersect(setB).Count();
        int union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    public static bool ContainsTimestamps(string lrc) =>
        Regex.IsMatch(lrc, @"\[\d{2}:\d{2}\.\d{2,3}\]");

    public static string CleanLrcMeta(string lrcContent)
    {
        var lines = lrcContent.Split('\n');
        var sb = new StringBuilder();

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // 跳过元数据标签行（ti/ar/al/by/offset/length 等）
            if (Regex.IsMatch(line, @"^\[(ti|ar|al|by|offset|length):", RegexOptions.IgnoreCase))
                continue;

            // 提取标准时间戳行
            var m = Regex.Match(line, @"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)");
            if (!m.Success) continue;

            string text = m.Groups[4].Value.Trim();
            if (string.IsNullOrEmpty(text) || IsMetaDataLine(text))
                continue;

            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }
    public static bool IsSameLanguage(string textA, string textB)
    {
        // 简单判断：统计中文字符占比，若一方 >70% 且另一方 <30%，则视为语言不一致
        static double ChineseRatio(string s)
        {
            int cnt = 0, total = 0;
            foreach (char c in s)
            {
                if (c >= 0x4e00 && c <= 0x9fff) cnt++;
                total++;
            }
            return total == 0 ? 0 : (double)cnt / total;
        }

        double rA = ChineseRatio(textA);
        double rB = ChineseRatio(textB);

        // 双方都是中文或双方都是非中文，视为语言一致
        bool aIsChinese = rA > 0.5;
        bool bIsChinese = rB > 0.5;
        return aIsChinese == bIsChinese;
    }
    private static bool IsMetaDataLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        text = text.TrimStart();
        foreach (var keyword in MetaKeywords)
        {
            if (Regex.IsMatch(text, $@"^{Regex.Escape(keyword)}\s*[：:]", RegexOptions.IgnoreCase))
                return true;
            if (Regex.IsMatch(text, $@"^{Regex.Escape(keyword)}\s+", RegexOptions.IgnoreCase))
                return true;
            if (string.Equals(text, keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly string[] MetaKeywords = {
        "作词","作曲","作词人","作曲人","词曲","词曲作者","曲作者","词作者",
        "编曲","编曲人","改编","重新编曲","译词","填词","原词","原著",
        "制作人","音乐制作人","制作","制作公司","制作室","联合制作人","执行制作人","助理制作人",
        "监制","出品","出品人","出品方","出品公司","总监制","总策划","执行监制",
        "企划","策划","统筹","协调","经纪","经纪人","宣发","宣传","发行统筹","企宣",
        "音乐总监","艺术总监","创意总监","视觉总监","音乐指导","配唱制作人","声乐指导",
        "艺人","艺术家","表演者","歌手","演唱","主唱","合唱","伴唱","和声","和音","和声编写","和声设计","伴唱编写",
        "人声","人声工程","人声录音","人声处理","人声指导","合唱指挥",
        "吉他","吉他手","电吉他","木吉他","古典吉他","12弦吉他","滑棒吉他",
        "贝斯","贝斯手","无品贝斯","合成贝斯","低音提琴",
        "鼓","鼓手","打击乐","打击乐手","架子鼓","电子鼓","鼓机",
        "键盘","键盘手","钢琴","三角钢琴","立式钢琴","电钢琴","合成器","MIDI键盘",
        "大提琴","中提琴","小提琴","低音提琴","竖琴","管弦乐","管弦乐团",
        "弦乐","弦乐编写","弦乐指导","弦乐团","弦乐演奏","弦乐录音","弦乐四重奏",
        "乐团","管乐团","民乐团","交响乐团","爱乐乐团","室内乐团","合唱团",
        "长笛","短笛","单簧管","双簧管","英国管","大管","萨克斯","高音萨克斯","中音萨克斯","次中音萨克斯","上低音萨克斯",
        "小号","短号","富鲁格号","长号","低音长号","圆号","大号","上低音号","次中音号",
        "口琴","手风琴","班多纽手风琴","口风琴",
        "二胡","板胡","京胡","高胡","中胡","马头琴","冬不拉","琵琶","柳琴","阮","中阮","大阮","三弦","月琴",
        "古筝","古琴","瑟","扬琴","箜篌","笛子","箫","唢呐","笙","埙","巴乌","葫芦丝",
        "尤克里里","曼陀林","班卓琴","卡林巴","钢舌鼓","手碟",
        "定音鼓","小军鼓","大军鼓","通通鼓","康加鼓","邦戈鼓","箱鼓","非洲鼓","铃鼓","三角铁","响板","沙锤","刮壶","木鱼","碰铃","风铃","牛铃","梆子",
        "演奏","独奏","合奏","齐奏","重奏","即兴",
        "录音","录音师","录音室","录音工程师","录音助理","录音指导","录制","录制人","录音棚",
        "混音","混音师","混音室","混音工程师","混音协助","混缩","缩混","缩混工程师",
        "母带","母带工程师","母带工程","项目统筹","母带处理","母带工作室","后期","后期制作","后期处理","后期混音",
        "声音设计","音效","人声录音室","和声录音室","音频编辑","音频剪辑","修音","音准修正","节奏修正","量化",
        "降噪","去齿音","去嘶声","混响","延迟","合唱效果","镶边","相移",
        "压缩","限幅","均衡","激励","立体声展宽","声像","自动化",
        "母带预处理","DDP制作","ISRC嵌入","CD文本","元数据编辑",
        "杜比全景声","空间音频","沉浸式音频","环绕声","立体声","单声道",
        "版权","版权方","版权代理","著作权","词曲版权","录音版权","影像版权",
        "唱片公司","厂牌","发行","发行方","发行公司","音乐发行",
        "出版社","出版","授权","独家授权","非独家授权","再版","翻版",
        "ISRC","ISWC","UPC","条形码","EAN","JAN","专辑编号","编号","目录编号",
        "实体发行","数字发行","卡带","黑胶唱片","彩胶","CD","DVD","蓝光",
        "全球发行","地区发行","代理发行","许可","采样许可",
        "专辑","专辑名称","流派","风格","语种","语言","时长","长度","曲目","音轨号",
        "发行日期","发行时间","制作日期","录制日期","上线日期","年份",
        "版本","初回限定盘","通常盘","豪华版","重制版","Remastered","数字版","限量版",
        "介质","比特率","采样率","位深","声道","文件格式","文件大小",
        "碟片号","光盘号","ISWC","作品编码",
        "歌词制作","歌词编辑","歌词贡献","歌词整理","歌词翻译","翻译","歌词校对","校对",
        "歌词提供","滚动歌词","LRC制作","LRC","QRC","KRC","逐字歌词","动态歌词","同步歌词",
        "Lyric by","Lyrics by","歌词：","歌词:","歌词上传","歌词贡献者","听写歌词","歌词时间戳",
        "来自","QQ音乐","网易云音乐","酷狗","酷我","虾米","咪咕音乐","千千音乐",
        "Spotify","Apple Music","YouTube","Tidal","Amazon Music","Deezer","Pandora",
        "KKBOX","Melon","Genie","LINE MUSIC","汽水音乐",
        "抖音","TikTok","Bilibili","微博","快手",
        "仅供","版权声明","非商用","试听","预览","推广",
        "备注","附注","鸣谢","特别感谢","协力","协力单位","参与人员","职员表","信息","数据","标签",
        "原唱","翻唱","原曲","采样","引用","采样来源",
        "封面设计","插画","摄影","造型","化妆","发型","美术设计","平面设计","文案",
        "提供","场地提供","乐器提供","服装提供","赞助","致敬","纪念",
        "Composer","Songwriter","Lyricist","Words by","Music by","Written by",
        "Arranger","Orchestrated by","Programmed by","Sound Design by",
        "Producer","Co-Producer","Executive Producer","Associate Producer","Line Producer",
        "Vocal","Singer","Featuring","Feat.","Ft.","With","And","Vs.","Versus",
        "Artist","Performer","Primary Artist","Guest Artist",
        "Album","EP","Single","Compilation","Soundtrack","OST","Original Soundtrack",
        "Label","Publisher","Copyright","Phonographic Copyright","Master","Publishing",
        "ISRC","ISWC","UPC","EAN","Track","Duration","Genre","Language",
        "Thanks","Note","Staff","Credit","Provided by","Courtesy of",
        "Translater","Translation","Edit","Editor","Source","Platform",
        "Apple","Spotify","YouTube","Tidal","Deezer","Amazon","Pandora",
        "Mastering","Mixing","Engineer","Assistant Engineer","Mastered by","Mixed by","Recorded by",
        "Studio","Recording Studio","Mixing Studio","Mastering Studio",
        "Guitar","Acoustic Guitar","Electric Guitar","Bass","Drums","Keyboard","Piano",
        "Strings","Orchestra","Choir","Backing Vocals","Horn","Flute","Saxophone","Trumpet","Violin","Cello",
        "Conductor","Directed by","Score","Film Score","BGM","Background Music","Theme Song",
        "Remix","Extended Mix","Radio Edit","Acoustic Version","Live Version","Studio Version","Demo","Cover",
        "Intro","Outro","Interlude","Bridge","Chorus","Verse","Hook","Solo","Duet",
        "A&R","Management","Booking","Agency","Creative Director","Art Direction","Photography by","Artwork by",
        "Lyric Video","Music Video","Official Video","Visualizer",
        "DAW","Pro Tools","Logic Pro","Ableton","Cubase","FL Studio",
        "All Rights Reserved","Public Domain","Creative Commons","CC BY","CC BY-SA","CC BY-NC","CC0",
        "Explicit","Clean","Instrumental","Off Vocal","Karaoke","OP","ED","Insert Song","Character Song",
        "Image Song","Theme Song","Ending Theme","Opening Theme","OP/SP"
    };
}