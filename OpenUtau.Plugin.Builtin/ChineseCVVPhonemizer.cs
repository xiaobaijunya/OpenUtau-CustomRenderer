using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Plugin.Builtin {
    /// <summary>
    /// Chinese 十月式整音扩张 CVV Phonemizer.
    /// <para>It works by spliting "duang" to "duang" + "_ang", to produce the proper tail sound.</para>
    /// </summary>
    [Phonemizer("Chinese CVV (十月式整音扩张) Phonemizer", "ZH CVV", language: "ZH")]
    public class ChineseCVVMonophonePhonemizer : MonophonePhonemizer
    {
        public ChineseCVVMonophonePhonemizer() {
            ConsonantLength = 120;    
        }

        protected override IG2p LoadG2p() {
            var g2ps = new List<IG2p>();

            // 方案A：外部 dic.txt 优先。
            // 只有在 dic.txt 中查不到该歌词时，才回落到音素器自带的硬编码词典。
            if (singer != null && singer.Found && singer.Loaded) {
                string file = Path.Combine(singer.Location, "dic.txt");
                if (File.Exists(file)) {
                    try {
                        g2ps.Add(new DicTxtG2p(file));
                    } catch (Exception e) {
                        Log.Error(e, $"Failed to load {file}");
                    }
                }
            }

            // 硬编码默认音素词典，直接根据歌词匹配 _V，作为兜底。
            g2ps.Add(new ChineseCVVG2p());

            return new G2pFallbacks(g2ps.ToArray());
        }

        protected override Dictionary<string, string[]> LoadVowelFallbacks() {
            return "_un=_en;_uai=_ai".Split(';')
                .Select(entry => entry.Split('='))
                .ToDictionary(parts => parts[0], parts => parts[1].Split(','));
        }

        public override void SetUp(Note[][] groups, UProject project, UTrack track) {
            BaseChinesePhonemizer.RomanizeNotes(groups);
        }

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevNeighbours) {
            string lyric = notes[0].lyric?.Trim() ?? string.Empty;

            // ===== 收尾音符：R / B / - =====
            // 与 CVVC 一致：不再由前一个音符生成 "_V" 尾音，
            // 而是由本音符生成 "<前一元音> R" / "<前一元音> B" / "<前一元音> -"。
            // 前一元音取自 dic 第二列（去掉开头的下划线），例如 "_ai" -> "ai"。
            if (IsEndingLyric(lyric)) {
                string ending = lyric == "-" ? "-" : lyric.ToUpperInvariant();
                string? prevVowel = prevNeighbour == null ? null : GetTailVowel(prevNeighbour.Value);
                if (!string.IsNullOrEmpty(prevVowel)) {
                    var endAttr = notes[0].phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
                    string alias = $"{prevVowel} {ending}";
                    if (singer != null &&
                        singer.TryGetMappedOto(alias, notes[0].tone + endAttr.toneShift, endAttr.voiceColor, out var endOto)) {
                        alias = endOto.Alias;
                    }
                    return new Result {
                        phonemes = new Phoneme[] {
                            new Phoneme() {
                                phoneme = alias,
                                position = 0,
                                expressions = new List<PhonemeExpression>() {
                                    new PhonemeExpression() { abbr = Core.Format.Ustx.PHTP, value = 2 }
                                }
                            }
                        }
                    };
                }
                // 无法解析前一元音时，回退到基类行为。
                return base.Process(notes, prev, next, prevNeighbour, nextNeighbour, prevNeighbours);
            }

            var result = base.Process(notes, prev, next, prevNeighbour, nextNeighbour, prevNeighbours);
            int totalDuration = notes.Sum(n => n.duration);
            int endTick = notes[0].position + totalDuration;

            var phonemeList = new List<Phoneme>(result.phonemes);

            // 若后一个音符是收尾音符(R/B/-)，本音符不再生成 "_V" 尾音，
            // 仅保留 CV 音素（尾音交由后面的收尾音符处理）。
            if (nextNeighbour != null && IsEndingLyric(nextNeighbour.Value.lyric)) {
                if (phonemeList.Count > 1) {
                    phonemeList.RemoveRange(1, phonemeList.Count - 1);
                }
                for (int i = 0; i < phonemeList.Count; i++) {
                    var p = phonemeList[i];
                    p.position = 0;
                    p.expressions = new List<PhonemeExpression>() {
                        new PhonemeExpression() { abbr = Core.Format.Ustx.PHTP, value = 0 }
                    };
                    phonemeList[i] = p;
                }
                return new Result { phonemes = phonemeList.ToArray() };
            }

            // 预先找出 CV 和 _V 的 phoneme 名称
            string? cvPhoneme = null;
            string? vPhoneme = null;
            foreach (var p in phonemeList) {
                if (p.phoneme.StartsWith("_")) {
                    vPhoneme = p.phoneme;
                } else if (cvPhoneme == null) {
                    cvPhoneme = p.phoneme;
                }
            }

            // 先验证 _V 在 oto 中是否存在，若不存在则不生成
            bool vHasOto = false;
            if (vPhoneme != null && singer != null && timeAxis != null) {
                vHasOto = singer.TryGetMappedOto(vPhoneme, notes[0].tone, out _);
            }
            

            // 仅当 _V 存在时才计算各项长度
            int cvVowelLen = 0;
            int vTailLen = 0;
            int nextFirstPreutter = 0;
            int nextFirstOverlap = 0;
            int nextFirsttotal = 0;

            if (vHasOto) {
                // CV 的 Preutter 后面的元音长度
                if (cvPhoneme != null) {
                    if (singer.TryGetMappedOto(cvPhoneme, notes[0].tone, out var cvOto)) {
                        cvVowelLen = CalcTailLen(cvOto, endTick, timeAxis);
                    }
                }

                // _V 的 Preutter 后面的长度
                if (vPhoneme != null ) {
                    if (singer.TryGetMappedOto(vPhoneme, notes[0].tone, out var vOto)) {
                        vTailLen = CalcTailLen(vOto, endTick, timeAxis);
                    }
                }

                // 下一个音符的第一个音素的 Preutter 长度（受 VEL 影响）
                if (nextNeighbour != null) {
                    string nextLyric = nextNeighbour.Value.lyric;
                    string[]? nextSymbols = g2p?.Query(nextLyric.ToLowerInvariant());
                    string nextFirst = (nextSymbols != null && nextSymbols.Length > 0)
                        ? nextSymbols[0] : nextLyric;
                    if (singer.TryGetMappedOto(nextFirst, nextNeighbour.Value.tone, out var nextOto)) {
                        nextFirstPreutter = timeAxis.MsToTickAt(nextOto.Preutter, endTick);
                        nextFirstOverlap = timeAxis.MsToTickAt(nextOto.Overlap, endTick);
                        // 应用 VEL（consonantStretchRatio），参考 CVVC 的 vcLen 处理
                        var nextAttr = nextNeighbour.Value.phonemeAttributes
                            ?.FirstOrDefault(attr => attr.index == 0) ?? default;
                        double stretch = nextAttr.consonantStretchRatio ?? 1;
                        // 辅音越长(vel<100, stretch>1) → 下一个音占空间越大 → _V 应越短
                        nextFirstPreutter = Convert.ToInt32(nextFirstPreutter * stretch);
                        nextFirsttotal = Convert.ToInt32((nextFirstPreutter + nextFirstOverlap) * stretch);
                    }
                }
            }

            for (int i = phonemeList.Count - 1; i >= 0; i--) {
                var p = phonemeList[i];
                bool isV = p.phoneme.StartsWith("_");

                if (isV) {
                    // ===== _V（尾部元音音素）=====
                    p.expressions = new List<PhonemeExpression>() {
                        new PhonemeExpression() { abbr = Core.Format.Ustx.PHTP, value = 2 }
                    };

                    int vLength;
                    int threshold = (cvVowelLen + vTailLen + nextFirstPreutter);
                    threshold = (int)(threshold * 2);
                    int totalDuration2 = totalDuration - nextFirstPreutter;

                    if (totalDuration2 > threshold) {
                        vLength = totalDuration2 / 4   + nextFirstPreutter;
                    } else {
                        vLength = vTailLen + nextFirstPreutter;
                    }

                    vLength = Math.Min((int)(totalDuration / 1.5) , Math.Max(1, vLength));
                    p.position = totalDuration - vLength;
                    phonemeList[i] = p;
                } else {
                    // ===== CV（完整音节，如 "duang"）=====
                    p.position = 0;
                    p.expressions = new List<PhonemeExpression>() {
                        new PhonemeExpression() { abbr = Core.Format.Ustx.PHTP, value = 0 }
                    };
                    phonemeList[i] = p;
                }
            }
            return new Result { phonemes = phonemeList.ToArray() };
        }

        // 计算 oto 中 Preutter 后面的有效长度（ticks）
        private static int CalcTailLen(UOto oto, int endTick, TimeAxis timeAxis) {
            double ms;
            if (oto.Cutoff >= 0) {
                // Cutoff ≥ 0：绝对值位置，有效音频到 Cutoff 为止
                ms = oto.Cutoff -  oto.Preutter;
            } else {
                // Cutoff < 0：相对值，有效音频长度 = |Cutoff|
                ms = -oto.Cutoff - oto.Preutter;
            }
            return ms > 0 ? timeAxis.MsToTickAt(ms, endTick) : 0;
        }

        /// <summary>
        /// 是否为收尾音符：R / B / -（大小写不敏感）。
        /// </summary>
        private static bool IsEndingLyric(string lyric) {
            if (string.IsNullOrEmpty(lyric)) {
                return false;
            }
            var l = lyric.Trim();
            return l == "-"
                || l.Equals("R", StringComparison.OrdinalIgnoreCase)
                || l.Equals("B", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 取音符拆音后的尾音（dic 第二列 / 内置词典的 "_V"），并去掉开头的下划线。
        /// 例如 "duang" -> "ang"，"ai" -> "ai"。
        /// </summary>
        private string? GetTailVowel(Note note) {
            if (g2p != null) {
                var symbols = g2p.Query(note.lyric.ToLowerInvariant());
                if (symbols != null && symbols.Length > 1) {
                    return symbols[1].TrimStart('_');
                }
            }
            if (!string.IsNullOrEmpty(note.phoneticHint)) {
                var hint = note.phoneticHint
                    .Split()
                    .Where(s => s.StartsWith("_"))
                    .ToArray();
                if (hint.Length > 0) {
                    return hint.Last().TrimStart('_');
                }
            }
            return null;
        }
    }
    
    /// <summary>
    /// 硬编码的默认音素词典，直接根据歌词匹配 _V。
    /// </summary>
    class ChineseCVVG2p : IG2p {
        static readonly Dictionary<string, string> map = new Dictionary<string, string> {
            {"ai","_ai"},{"an","_an"},{"ao","_ao"},{"ang","_ang"},{"bai","_ai"},{"ban","_an"},{"bang","_ang"},{"bao","_ao"},{"bei","_ei"},{"ben","_en"},{"beng","_eng"},{"bian","_en2"},{"biao","_ao"},{"bin","_in"},{"bing","_ing"},{"cai","_ai"},{"can","_an"},{"cang","_ang"},{"cao","_ao"},{"cen","_en"},{"ceng","_eng"},{"chai","_ai"},{"chan","_an"},{"chang","_ang"},{"chao","_ao"},{"chen","_en"},{"cheng","_eng"},{"chong","_ong"},{"chou","_ou"},{"chuai","_ai"},{"chuan","_an"},{"chuang","_ang"},{"chui","_ei"},{"chun","_en"},{"cong","_ong"},{"cou","_ou"},{"cuan","_an"},{"cui","_ei"},{"cun","_en"},{"dai","_ai"},{"dan","_an"},{"dang","_ang"},{"dao","_ao"},{"den","_en"},{"dei","_ei"},{"deng","_eng"},{"dian","_en2"},{"diao","_ao"},{"ding","_ing"},{"diu","_ou"},{"dong","_ong"},{"dou","_ou"},{"duan","_an"},{"dui","_ei"},{"dun","_en"},{"er","_er"},{"en","_en"},{"eng","_eng"},{"fan","_an"},{"fang","_ang"},{"fei","_ei"},{"fen","_en"},{"feng","_eng"},{"fou","_ou"},{"gai","_ai"},{"gan","_an"},{"gang","_ang"},{"gao","_ao"},{"gei","_ei"},{"gen","_en"},{"geng","_eng"},{"gong","_ong"},{"gou","_ou"},{"guai","_ai"},{"guan","_an"},{"guang","_ang"},{"gui","_ei"},{"gun","_en"},{"hai","_ai"},{"han","_an"},{"hang","_ang"},{"hao","_ao"},{"hei","_ei"},{"hen","_en"},{"heng","_eng"},{"hong","_ong"},{"hou","_ou"},{"huai","_ai"},{"huan","_an"},{"huang","_ang"},{"hui","_ei"},{"hun","_en"},{"jian","_en2"},{"jiang","_ang"},{"jiao","_ao"},{"jin","_in"},{"jing","_ing"},{"jiong","_iong"},{"jiu","_ou"},{"juan","_an"},{"jun","_vn"},{"kai","_ai"},{"kan","_an"},{"kang","_ang"},{"kao","_ao"},{"ken","_en"},{"keng","_eng"},{"kong","_ong"},{"kou","_ou"},{"kuai","_ai"},{"kuan","_an"},{"kuang","_ang"},{"kui","_ei"},{"kun","_en"},{"lai","_ai"},{"lan","_an"},{"lang","_ang"},{"lao","_ao"},{"lei","_ei"},{"leng","_eng"},{"lian","_en2"},{"liang","_ang"},{"liao","_ao"},{"lin","_in"},{"ling","_ing"},{"liu","_ou"},{"long","_ong"},{"lou","_ou"},{"luan","_an"},{"lun","_en"},{"mai","_ai"},{"man","_an"},{"mang","_ang"},{"mao","_ao"},{"mei","_ei"},{"men","_en"},{"meng","_eng"},{"mian","_en2"},{"miao","_ao"},{"min","_in"},{"ming","_ing"},{"miu","_ou"},{"mou","_ou"},{"nai","_ai"},{"nan","_an"},{"nang","_ang"},{"nao","_ao"},{"nei","_ei"},{"nen","_en"},{"neng","_eng"},{"nian","_en2"},{"niang","_ang"},{"niao","_ao"},{"nin","_in"},{"ning","_ing"},{"niu","_ou"},{"nong","_ong"},{"nou","_ou"},{"nuan","_an"},{"nun","_en"},{"ou","_ou"},{"ong","_ong"},{"pai","_ai"},{"pan","_an"},{"pang","_ang"},{"pao","_ao"},{"pei","_ei"},{"pen","_en"},{"peng","_eng"},{"pian","_en2"},{"piao","_ao"},{"pin","_in"},{"ping","_ing"},{"pou","_ou"},{"qian","_en2"},{"qiang","_ang"},{"qiao","_ao"},{"qin","_in"},{"qing","_ing"},{"qiong","_iong"},{"qiu","_ou"},{"quan","_an"},{"qun","_vn"},{"ran","_an"},{"rang","_ang"},{"rao","_ao"},{"ren","_en"},{"reng","_eng"},{"rong","_ong"},{"rou","_ou"},{"ruan","_an"},{"rui","_ei"},{"run","_en"},{"sai","_ai"},{"san","_an"},{"sang","_ang"},{"sao","_ao"},{"sen","_en"},{"seng","_eng"},{"shai","_ai"},{"shan","_an"},{"shang","_ang"},{"shao","_ao"},{"shei","_ei"},{"shen","_en"},{"sheng","_eng"},{"shou","_ou"},{"shuai","_ai"},{"shuan","_an"},{"shuang","_ang"},{"shui","_ei"},{"shun","_en"},{"song","_ong"},{"sou","_ou"},{"suan","_an"},{"sui","_ei"},{"sun","_en"},{"tai","_ai"},{"tan","_an"},{"tang","_ang"},{"tao","_ao"},{"teng","_eng"},{"tian","_en2"},{"tiao","_ao"},{"ting","_ing"},{"tong","_ong"},{"tou","_ou"},{"tuan","_an"},{"tui","_ei"},{"tun","_en"},{"wai","_ai"},{"wan","_an"},{"wang","_ang"},{"wei","_ei"},{"wen","_en"},{"weng","_eng"},{"xian","_en2"},{"xiang","_ang"},{"xiao","_ao"},{"xin","_in"},{"xing","_ing"},{"xiong","_iong"},{"xiu","_ou"},{"xuan","_an"},{"xun","_vn"},{"yan","_en2"},{"yang","_ang"},{"yao","_ao"},{"yin","_in"},{"ying","_ing"},{"yong","_ong"},{"you","_ou"},{"yuan","_an"},{"yun","_vn"},{"zai","_ai"},{"zan","_an"},{"zang","_ang"},{"zao","_ao"},{"zei","_ei"},{"zen","_en"},{"zeng","_eng"},{"zhai","_ai"},{"zhan","_an"},{"zhang","_ang"},{"zhao","_ao"},{"zhei","_ei"},{"zhen","_en"},{"zheng","_eng"},{"zhong","_ong"},{"zhou","_ou"},{"zhuai","_ai"},{"zhuan","_an"},{"zhuang","_ang"},{"zhui","_ei"},{"zhun","_en"},{"zong","_ong"},{"zou","_ou"},{"zuan","_an"},{"zui","_ei"},{"zun","_en"},
        };

        public bool IsVowel(string phoneme) {
            return !phoneme.StartsWith("_");
        }

        public bool IsGlide(string phoneme) {
            return false;
        }

        public string[] Query(string lyric) {
            if (map.TryGetValue(lyric, out var tail)) {
                return new string[] { lyric, tail };
            }
            return new string[] { lyric };
        }

        public bool IsValidSymbol(string symbol) {
            return true;
        }

        public string[] UnpackHint(string hint, char separator = ' ') {
            return hint.Split(separator).ToArray();
        }
    }

    class DicTxtG2p : IG2p {
        private Dictionary<string, string> map = new Dictionary<string, string>();

        public DicTxtG2p(string filePath) {
            foreach (var line in File.ReadAllLines(filePath)) {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) {
                    continue;
                }
                var parts = trimmed.Split(' ');
                if (parts.Length >= 2) {
                    map[parts[0]] = parts[1];
                }
            }
        }

        public bool IsVowel(string phoneme) {
            // 与内置词典保持一致："_V" 尾音视为辅音，其余视为元音。
            return !phoneme.StartsWith("_");
        }

        public bool IsGlide(string phoneme) {
            return false;
        }

        public string[] Query(string lyric) {
            if (map.TryGetValue(lyric, out var tail)) {
                return new string[] { lyric, tail };
            }
            return null;
        }

        public bool IsValidSymbol(string symbol) {
            return true;
        }

        public string[] UnpackHint(string hint, char separator = ' ') {
            return hint.Split(separator).ToArray();
        }
    }
}
