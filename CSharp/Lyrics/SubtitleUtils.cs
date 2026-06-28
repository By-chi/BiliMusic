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

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = ToSimplified(text);
        text = Regex.Replace(text, @"\s+", "");
        text = Regex.Replace(text, @"[\p{P}\p{S}]", "");
        return text;
    }

    public static double CalculateSimilarity(string a, string b)
    {
        a = NormalizeText(a);
        b = NormalizeText(b);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;

        if (a.Length < 2 || b.Length < 2)
            return a == b ? 1.0 : 0.0;

        var setA = new HashSet<string>();
        var setB = new HashSet<string>();
        for (int i = 0; i < a.Length - 1; i++) setA.Add(a.Substring(i, 2));
        for (int i = 0; i < b.Length - 1; i++) setB.Add(b.Substring(i, 2));

        int inter = setA.Intersect(setB).Count();
        int union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    public static List<(double time, string text)> AlignBilibiliToExternalMulti(
        List<string> externalLines,
        List<(double time, string text)> bilibiliEntries,
        double similarityThreshold = 0.4,
        double fallbackThreshold = 0.3)
    {
        GD.Print("[DEBUG-ALIGN] ========== 开始最终对齐（跳过前奏） ==========");
        if (externalLines == null || externalLines.Count == 0)
            return new List<(double time, string text)>();
        if (bilibiliEntries == null || bilibiliEntries.Count == 0)
            return externalLines.Select(line => (0.0, line)).ToList();

        var biliItems = bilibiliEntries
            .Select(x => (time: x.time, norm: NormalizeText(x.text), raw: x.text))
            .Where(x => !string.IsNullOrWhiteSpace(x.norm) && x.norm.Length >= 2
                        && !Regex.IsMatch(x.norm, @"^[\d\.\,\;\:\!?\-\+\(\)\[\]\{\}\s]+$"))
            .ToList();

        int nExt = externalLines.Count;
        int nBili = biliItems.Count;
        var result = new List<(double time, string text)>(nExt);
        var usedBiliIndices = new HashSet<int>();
        double lastMatchedTime = -1.0;

        // *** 新增：计算全局起始索引，跳过 B 站前奏（英文独白等） ***
        int globalStartIdx = 0;
        if (externalLines.Count > 0)
        {
            string firstExtNorm = NormalizeText(externalLines[0]);
            for (int k = 0; k < nBili; k++)
            {
                if (CalculateSimilarity(firstExtNorm, biliItems[k].norm) > 0.2)
                {
                    globalStartIdx = k;
                    break;
                }
            }
            if (globalStartIdx > 0)
            {
                // 将最后匹配时间设为前一帧的时间，让算法自动跳过前面的行
                lastMatchedTime = biliItems[globalStartIdx - 1].time;
                GD.Print($"   => 跳过前奏，从 B站[{globalStartIdx}] 开始 ({biliItems[globalStartIdx].time:F2}s)");
            }
        }

        for (int i = 0; i < nExt; i++)
        {
            string extNorm = NormalizeText(externalLines[i]);
            GD.Print($"\n[DEBUG-ALIGN] 外部[{i}] '{externalLines[i]}'");

            double bestSimUnused = 0;
            int bestStartUnused = -1, bestEndUnused = -1;

            // 1. 搜索未使用的行
            for (int k = 0; k < nBili; k++)
            {
                if (usedBiliIndices.Contains(k)) continue;
                if (biliItems[k].time <= lastMatchedTime) continue;

                var merged = new StringBuilder();
                int end = k;
                while (end < nBili && !usedBiliIndices.Contains(end))
                {
                    if (merged.Length > 0) merged.Append(" ");
                    merged.Append(biliItems[end].norm);
                    double sim = CalculateSimilarity(extNorm, merged.ToString());
                    if (sim >= similarityThreshold)
                    {
                        if (bestStartUnused == -1 || k < bestStartUnused || (k == bestStartUnused && sim > bestSimUnused))
                        {
                            bestSimUnused = sim;
                            bestStartUnused = k;
                            bestEndUnused = end;
                        }
                    }
                    else if (sim > bestSimUnused && bestStartUnused == -1)
                    {
                        bestSimUnused = sim;
                        bestStartUnused = k;
                        bestEndUnused = end;
                    }
                    if (merged.Length > extNorm.Length * 2.5 && end > k) break;
                    end++;
                }
            }

            // 2. 如果达标，直接使用
            if (bestStartUnused >= 0 && bestSimUnused >= similarityThreshold)
            {
                result.Add((biliItems[bestStartUnused].time, externalLines[i]));
                for (int u = bestStartUnused; u <= bestEndUnused; u++)
                    usedBiliIndices.Add(u);
                lastMatchedTime = biliItems[bestStartUnused].time;
                GD.Print($"   => 匹配未使用 B站[{bestStartUnused}..{bestEndUnused}] 时间={biliItems[bestStartUnused].time:F2}s sim={bestSimUnused:F3}");
                continue;
            }

            // 3. Fallback 未使用行
            if (bestStartUnused >= 0 && bestSimUnused >= fallbackThreshold)
            {
                result.Add((biliItems[bestStartUnused].time, externalLines[i]));
                for (int u = bestStartUnused; u <= bestEndUnused; u++)
                    usedBiliIndices.Add(u);
                lastMatchedTime = biliItems[bestStartUnused].time;
                GD.Print($"   => Fallback未使用 B站[{bestStartUnused}..{bestEndUnused}] 时间={biliItems[bestStartUnused].time:F2}s sim={bestSimUnused:F3}");
                continue;
            }

            // 4. 尝试重用已使用的行（时间 >= lastMatchedTime）
            double bestSimReuse = 0;
            int bestStartReuse = -1, bestEndReuse = -1;
            for (int k = 0; k < nBili; k++)
            {
                if (biliItems[k].time < lastMatchedTime) continue;

                var merged = new StringBuilder();
                int end = k;
                while (end < nBili)
                {
                    if (merged.Length > 0) merged.Append(" ");
                    merged.Append(biliItems[end].norm);
                    double sim = CalculateSimilarity(extNorm, merged.ToString());
                    if (sim >= similarityThreshold)
                    {
                        if (bestStartReuse == -1 || k < bestStartReuse || (k == bestStartReuse && sim > bestSimReuse))
                        {
                            bestSimReuse = sim;
                            bestStartReuse = k;
                            bestEndReuse = end;
                        }
                    }
                    else if (sim > bestSimReuse && bestStartReuse == -1)
                    {
                        bestSimReuse = sim;
                        bestStartReuse = k;
                        bestEndReuse = end;
                    }
                    if (merged.Length > extNorm.Length * 2.5 && end > k) break;
                    end++;
                }
            }

            if (bestStartReuse >= 0 && bestSimReuse >= similarityThreshold)
            {
                result.Add((biliItems[bestStartReuse].time, externalLines[i]));
                lastMatchedTime = biliItems[bestStartReuse].time;
                GD.Print($"   => 匹配重用 B站[{bestStartReuse}..{bestEndReuse}] 时间={biliItems[bestStartReuse].time:F2}s sim={bestSimReuse:F3}");
            }
            else if (bestStartReuse >= 0 && bestSimReuse >= fallbackThreshold)
            {
                result.Add((biliItems[bestStartReuse].time, externalLines[i]));
                lastMatchedTime = biliItems[bestStartReuse].time;
                GD.Print($"   => Fallback重用 B站[{bestStartReuse}..{bestEndReuse}] 时间={biliItems[bestStartReuse].time:F2}s sim={bestSimReuse:F3}");
            }
            else
            {
                double interpTime;
                // *** 新增：第一句插值时，使用跳过前奏后的第一个时间 ***
                if (result.Count == 0 && globalStartIdx > 0)
                    interpTime = biliItems[globalStartIdx].time;
                else
                    interpTime = InterpolateTime(i, result, bilibiliEntries, externalLines);

                result.Add((interpTime, externalLines[i]));
                GD.Print($"   => 无达标匹配 (未使用best={bestSimUnused:F3}, 重用best={bestSimReuse:F3})，插值时间={interpTime:F2}s");
            }
        }

        return result;
    }

    private static double InterpolateTime(int index,
        List<(double time, string text)> alignedSoFar,
        List<(double time, string text)> bilibiliEntries,
        List<string> externalLines)
    {
        int prevIndex = -1;
        for (int k = index - 1; k >= 0; k--)
        {
            if (k < alignedSoFar.Count && alignedSoFar[k].time >= 0)
            {
                prevIndex = k;
                break;
            }
        }

        if (prevIndex >= 0)
        {
            double prevTime = alignedSoFar[prevIndex].time;
            double avgInterval = 3.0;
            int matchedCount = 0;
            double totalInterval = 0;
            for (int m = 1; m < alignedSoFar.Count; m++)
            {
                double diff = alignedSoFar[m].time - alignedSoFar[m - 1].time;
                if (diff > 0 && diff < 30)
                {
                    totalInterval += diff;
                    matchedCount++;
                }
            }
            if (matchedCount > 0)
                avgInterval = totalInterval / matchedCount;

            int lineDiff = index - prevIndex;
            return prevTime + lineDiff * avgInterval;
        }

        if (bilibiliEntries.Count > 0)
            return bilibiliEntries[0].time;
        return 0;
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

            if (Regex.IsMatch(line, @"^\[(ti|ar|al|by|offset|length):", RegexOptions.IgnoreCase))
                continue;

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
        "男","女","合","童","独","领","齐","轮","对","重","高","低","主","伴","和",
        "男声","女声","合唱","独唱","对唱","重唱",
        "念白","独白","旁白","Rap","说唱",
        "前奏","间奏","尾奏","过门","桥段",
        "渐慢","渐强","渐弱","回原速","自由延长",
        "掌声","笑声","哭声","吼声","嘘声","哨声",
        "电话音","电台音","模糊音","失真音",
        "进鼓","进贝斯","进吉他","进弦乐","DROP",
        "词", "曲","作词","作曲","作词人","作曲人","词曲","词曲作者","曲作者","词作者",
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
        "Lyric by","Lyrics by","歌词","歌词:","歌词上传","歌词贡献者","听写歌词","歌词时间戳",
        "来自","QQ音乐","网易云音乐","酷狗","酷我","虾米","咪咕音乐","千千音乐",
        "Spotify","Apple Music","YouTube","Tidal","Amazon Music","Deezer","Pandora",
        "KKBOX","Melon","Genie","LINE MUSIC","汽水音乐",
        "抖音","TikTok","Bilibili","微博","快手",
        "仅供","版权声明","非商用","试听","预览","推广",
        "备注","附注","鸣谢","特别感谢","协力","协力单位","参与人员","职员表","信息","数据","标签",
        "原唱","翻唱","原曲","采样","引用","采样来源",
        "封面设计","插画","摄影","造型","化妆","发型","美术设计","平面设计","文案",
        "提供","场地提供","乐器提供","服装提供","赞助","致敬","纪念",
        "京二胡","革胡","低音革胡","坠琴","排鼓","堂鼓","云锣","铙钹","编钟","编磬",
        "工尺谱","减字谱","管风琴","羽管键琴","马林巴","颤音琴","钟琴","钢片琴",
        "前奏","间奏","尾奏","主歌","副歌","桥段","过门","总谱","分谱","配器","扒带","制谱","抄谱","谱务",
        "分轨","干声","湿声","相位","响度","动态","拟音","动效","贴唱","多轨","同期录音","分轨混音",
        "舞台监督","灯光师","舞美设计","道具设计","服装设计","造型设计","化妆师","发型师",
        "邻接权","表演权","广播权","信息网络传播权","改编权","署名权",
        "开盘带","DAT","MD","LD","VCD","SVCD","黑胶母盘","白板碟","宣传碟","见本盘",
        "打榜","榜单","乐评人","乐评","首发","独家首发","上线平台","推荐位",
        "戏曲指导","身段指导","唱腔设计","音乐设计","配乐指导","对白录音","动效录音","拟音棚",
        "录音制作者","录音制作者权","词曲代理","版权代理方","著作权集体管理",
        "混音助理","母带助理","录音文书","发行代号","条形码","厂牌编号","库存号",
        "盒装","套装","精装版","简装版","引进版","原装进口","港版","台版","大陆版",
        "演奏用琴","乐器提供","琴弦提供","鼓皮提供","音响工程","监听环境","声学设计",
        "Composed by","Composed",
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

    public static List<string> ExtractLyricLinesFromLrc(string cleanedLrc)
    {
        var lines = cleanedLrc.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lyricList = new List<string>();
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"^\[\d{2}:\d{2}\.\d{2,3}\](.*)");
            if (!m.Success) continue;
            string text = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            if (IsMetaDataLine(text)) continue;
            lyricList.Add(text);
        }
        if (lyricList.Count > 0 && lyricList[0].Contains(" - ") && lyricList[0].Length < 80)
            lyricList.RemoveAt(0);
        return lyricList;
    }
}