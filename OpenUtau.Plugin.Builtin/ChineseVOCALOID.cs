using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("Chinese CV-V-VC Phonemizer", "ZH CV-V-VC", language: "ZH")]
    public class ChineseVOCALOIDPhonemizer : Phonemizer {
        private Dictionary<string, string[]> phonemeDict = new Dictionary<string, string[]>();
        private Dictionary<string, string> vowelDict = new Dictionary<string, string>();
        private Dictionary<string, string> presampReplace = new Dictionary<string, string>();
        private USinger singer = null!;

        public override void SetSinger(USinger singer) {
            if (this.singer == singer) {
                return;
            }
            this.singer = singer;
            LoadDictionary();
            LoadPresamp();
        }

        private void LoadDictionary() {
            phonemeDict.Clear();
            vowelDict.Clear();
            
            if (singer == null || !singer.Found || !singer.Loaded) {
                return;
            }

            try {
                string file = Path.Combine(singer.Location, "dic.txt");
                if (!File.Exists(file)) {
                    Log.Warning($"dic.txt not found in {singer.Location}");
                    return;
                }

                using (var reader = new StreamReader(file, singer.TextFileEncoding)) {
                    string? line;
                    while ((line = reader.ReadLine()) != null) {
                        line = line.Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#")) {
                            continue;
                        }

                        var parts = line.Split(',');
                        if (parts.Length < 2) {
                            continue;
                        }

                        string lyric = parts[0].Trim();
                        string phonemesStr = parts[1].Trim();
                        string[] phonemes = phonemesStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        if (phonemes.Length == 3) {
                            phonemeDict[lyric] = phonemes;
                        } else if (phonemes.Length == 2) {
                            var modifiedPhonemes = new string[3];
                            modifiedPhonemes[0] = phonemes[0];
                            modifiedPhonemes[1] = "_" + phonemes[1];
                            modifiedPhonemes[2] = phonemes[1];
                            phonemeDict[lyric] = modifiedPhonemes;
                        } else if (phonemes.Length == 1) {
                            vowelDict[lyric] = phonemes[0];
                        }
                    }
                }
                Log.Information($"Loaded {phonemeDict.Count} phoneme entries and {vowelDict.Count} vowel entries from dic.txt");
            } catch (Exception e) {
                Log.Error(e, "Failed to load dic.txt");
            }
        }

        private void LoadPresamp() {
            presampReplace.Clear();

            if (singer == null || !singer.Found || !singer.Loaded) {
                return;
            }

            try {
                string file = Path.Combine(singer.Location, "presamp.ini");
                if (!File.Exists(file)) {
                    return;
                }

                using (var reader = new StreamReader(file, singer.TextFileEncoding)) {
                    var blocks = Ini.ReadBlocks(reader, file, @"\[\w+\]");

                    // Read [REPLACE] section for lyric replacements
                    var replaceBlock = blocks.Find(block => block.header == "[REPLACE]");
                    if (replaceBlock != null) {
                        foreach (var iniLine in replaceBlock.lines) {
                            var parts = iniLine.line.Split('=');
                            if (parts.Length >= 2) {
                                presampReplace[parts[0]] = parts[1];
                            }
                        }
                    }

                    // 先读取 [VOWEL] 节，暂存歌词→元音映射
                    var tempVowelDict = new Dictionary<string, string>();
                    var vowelBlock = blocks.Find(block => block.header == "[VOWEL]");
                    if (vowelBlock != null) {
                        foreach (var iniLine in vowelBlock.lines) {
                            var parts = iniLine.line.Split('=');
                            if (parts.Length >= 3) {
                                string[] sounds = parts[2].Split(',');
                                foreach (var sound in sounds) {
                                    if (!tempVowelDict.ContainsKey(sound)) {
                                        tempVowelDict[sound] = parts[0];
                                    }
                                }
                            }
                        }
                    }

                    // 再读取 [CONSONANT] 节，结合 tempVowelDict 构建 phonemeDict
                    var consonantBlock = blocks.Find(block => block.header == "[CONSONANT]");
                    if (consonantBlock != null) {
                        foreach (var iniLine in consonantBlock.lines) {
                            var parts = iniLine.line.Split('=');
                            if (parts.Length >= 2) {
                                string consonant = parts[0];
                                string[] sounds = parts[1].Split(',');
                                foreach (var sound in sounds) {
                                    string lyric = sound.Trim();
                                    if (string.IsNullOrEmpty(lyric)) {
                                        continue;
                                    }
                                    // 跳过已在 phonemeDict 中的（dic.txt 优先级更高）
                                    if (phonemeDict.ContainsKey(lyric)) {
                                        continue;
                                    }
                                    if (tempVowelDict.TryGetValue(lyric, out var vowel)) {
                                        // 同时存在于 C 和 V → 合并到 phonemeDict
                                        phonemeDict[lyric] = new[] { consonant, "_" + vowel, vowel };
                                    } else {
                                        // 仅存在于 C → V 部分置空
                                        phonemeDict[lyric] = new[] { consonant, "", "" };
                                    }
                                }
                            }
                        }
                    }

                    // 最后：仅将不在 C 列表中的纯元音放入 vowelDict
                    foreach (var kvp in tempVowelDict) {
                        if (!phonemeDict.ContainsKey(kvp.Key) && !vowelDict.ContainsKey(kvp.Key)) {
                            vowelDict[kvp.Key] = kvp.Value;
                        }
                    }
                }
                Log.Information($"Loaded presamp.ini with {presampReplace.Count} replacements, {phonemeDict.Count} phoneme entries");
            } catch (Exception e) {
                Log.Error(e, "Failed to load presamp.ini");
            }
        }

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevNeighbours) {
            var note = notes[0];
            string lyric = note.lyric;

            // Apply presamp replacement (exact match)
            if (presampReplace.TryGetValue(lyric, out var replacedLyric)) {
                lyric = replacedLyric;
            }

            var attr0 = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
            var attr1 = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == 1) ?? default;
            int tone = note.tone + attr0.toneShift;
            string color = attr0.voiceColor;
            double? stretchRatio = attr1.consonantStretchRatio;

            int totalDuration = notes.Sum(n => n.duration);

            // Handle rest note
            if (lyric == "-") {
                if (prevNeighbour != null) {
                    var prevLyric = prevNeighbour.Value.lyric;
                    if (phonemeDict.TryGetValue(prevLyric, out var prevPhonemes)) {
                        var (phoneme, _, _) = GetMappedPhoneme($"{prevPhonemes[2]} -", tone, color);
                        return new Result { phonemes = new[] { CreatePhoneme(phoneme, 0, 2) } };
                    } else if (vowelDict.TryGetValue(prevLyric, out var prevVowel)) {
                        var (phoneme, _, _) = GetMappedPhoneme($"{prevVowel} -", tone, color);
                        return new Result { phonemes = new[] { CreatePhoneme(phoneme, 0, 2) } };
                    }
                }
                return new Result { phonemes = new[] { CreatePhoneme("-", 0, 2) } };
            }

            bool isFirstNote = prevNeighbour == null;
            bool isLastNote = nextNeighbour == null;
            bool nextIsUnderscore = nextNeighbour != null && nextNeighbour.Value.lyric.StartsWith("_");

            var phonemes = new List<Phoneme>();

            // Handle underscore-prefixed lyrics (extended vowels like "_a")
            if (lyric.StartsWith("_")) {
                string baseLyric = lyric.Substring(1);
                var (startPhoneme, _, _) = GetMappedPhoneme(lyric, tone, color);
                phonemes.Add(CreatePhoneme(startPhoneme, 0, 2));

                bool nextIsVowel = nextNeighbour != null && vowelDict.ContainsKey(nextNeighbour.Value.lyric);
                if (!nextIsVowel && !nextIsUnderscore) {
                    if (isLastNote) {
                        // Uses "_lyric" for duration (original behavior) and "lyric" for phoneme lookup
                        int endDuration = GetLastPhonemeDuration(lyric, tone, stretchRatio);
                        int endStart = totalDuration - Math.Min(totalDuration / 6, endDuration);
                        var (endPhoneme, _, endFound) = GetMappedPhoneme($"{baseLyric} -", tone, color);
                        if (endFound) {
                            phonemes.Add(CreatePhoneme(endPhoneme, endStart, 2));
                        } else {
                            var (endPhoneme2, _, _) = GetMappedPhoneme($"{baseLyric} R", tone, color);
                            phonemes.Add(CreatePhoneme(endPhoneme2, endStart, 2));
                        }
                    } else {
                        AddTransitionToNext(baseLyric, nextNeighbour!.Value, tone, color, totalDuration, stretchRatio, phonemes);
                    }
                }
                return new Result { phonemes = phonemes.ToArray() };
            }

            bool isVowel = vowelDict.TryGetValue(lyric, out var vowel);

            if (isVowel) {
                // Start phoneme: connection from previous note
                if (isFirstNote) {
                    var (phoneme, _, _) = GetMappedPhoneme($"- {vowel}", tone, color);
                    phonemes.Add(CreatePhoneme(phoneme, 0, 1));
                } else {
                    string prevLastPhoneme = GetLastPhoneme(prevNeighbour!.Value.lyric);
                    var (phoneme, _, _) = GetMappedPhoneme($"{prevLastPhoneme} {vowel}", tone, color);
                    phonemes.Add(CreatePhoneme(phoneme, 0, 0));
                }

                // Stretch vowel (_V) - only add if a valid oto mapping exists
                string stretchVowel = "_" + vowel;
                string prevPhoneme = phonemes[phonemes.Count - 1].phoneme;
                int vowelStartDuration = GetVowelDuration(prevPhoneme, tone, stretchRatio);
                var (stretchPhoneme, _, stretchFound) = GetMappedPhoneme(stretchVowel, tone, color);
                if (stretchFound && vowelStartDuration < totalDuration / 2) {
                    // VV的生成
                    phonemes.Add(CreatePhoneme(stretchPhoneme, vowelStartDuration, 2));
                }

                if (nextIsUnderscore) {
                    return new Result { phonemes = phonemes.ToArray() };
                }

                // End phoneme or transition to next note
                if (isLastNote) {
                    AddEndingPhoneme(vowel!, tone, color, totalDuration, stretchRatio, phonemes);
                } else {
                    bool nextIsVowel = vowelDict.ContainsKey(nextNeighbour!.Value.lyric);
                    if (!nextIsVowel) {
                        AddTransitionToNext(vowel!, nextNeighbour!.Value, tone, color, totalDuration, stretchRatio, phonemes);
                    }
                }
            } else {
                // Fallback: direct oto lookup if lyric not in dictionaries
                if (!phonemeDict.TryGetValue(lyric, out var phonemeEntry)) {
                    var (phoneme, _, _) = GetMappedPhoneme(lyric, tone, color);
                    return new Result { phonemes = new[] { CreatePhoneme(phoneme, 0, 0) } };
                }

                string firstPhoneme = phonemeEntry[0];
                string stretchPhonemeName = phonemeEntry[1];
                string lastPhoneme = phonemeEntry[2];

                int firstPhonemeDuration = GetFirstPhonemeDuration(lyric, tone, stretchRatio);

                // Leading transition if first note
                if (isFirstNote) {
                    var (phoneme, _, _) = GetMappedPhoneme($"- {firstPhoneme}", tone, color);
                    phonemes.Add(CreatePhoneme(phoneme, -firstPhonemeDuration, 1));
                }

                // Main phoneme
                var (mainPhoneme, _, _) = GetMappedPhoneme(lyric, tone, color);
                phonemes.Add(CreatePhoneme(mainPhoneme, 0, 0));

                // Stretch vowel (_V) - only add if a valid oto mapping exists
                int stretchStart = GetVowelDuration(mainPhoneme, tone, stretchRatio);
                var (stretchMapped, _, stretchFound) = GetMappedPhoneme(stretchPhonemeName, tone, color);
                if (stretchFound && stretchStart < totalDuration / 2) {
                    phonemes.Add(CreatePhoneme(stretchMapped, stretchStart, 2));
                }

                if (!nextIsUnderscore) {
                    bool nextIsVowel = nextNeighbour != null && vowelDict.ContainsKey(nextNeighbour!.Value.lyric);
                    if (!nextIsVowel) {
                        if (isLastNote) {
                            AddEndingPhoneme(lastPhoneme, tone, color, totalDuration, stretchRatio, phonemes);
                        } else {
                            AddTransitionToNext(lastPhoneme, nextNeighbour!.Value, tone, color, totalDuration, stretchRatio, phonemes);
                        }
                    }
                }
            }

            return new Result { phonemes = phonemes.ToArray() };
        }

        private (string phonemeName, UOto? oto, bool found) GetMappedPhoneme(string phoneme, int tone, string color) {
            if (singer.TryGetMappedOto(phoneme, tone, color, out var oto)) {
                return (oto.Alias, oto, true);
            }
            
            for (int t = 1; t <= 36; t++) {
                int tone1 = tone - t;
                int tone2 = tone + t;
                if (singer.TryGetMappedOto(phoneme, tone1, color, out var oto2)) {
                    return (oto2.Alias, oto2, true);
                }
                if (singer.TryGetMappedOto(phoneme, tone2, color, out var oto3)) {
                    return (oto3.Alias, oto3, true);
                }
            }
            
            return (phoneme, null, false);
        }

        private Phoneme CreatePhoneme(string name, int position, int phtpValue) {
            return new Phoneme {
                phoneme = name,
                position = position,
                expressions = new List<PhonemeExpression>() {
                    new PhonemeExpression() { abbr = Core.Format.Ustx.PHTP, value = phtpValue }
                }
            };
        }

        private void AddEndingPhoneme(string phonemeBase, int tone, string color, int totalDuration, double? stretchRatio, List<Phoneme> phonemes) {
            int endDuration = GetLastPhonemeDuration(phonemeBase, tone, stretchRatio);
            int endStart = totalDuration - Math.Min(totalDuration / 6, endDuration);
            var (endPhoneme, _, endFound) = GetMappedPhoneme($"{phonemeBase} -", tone, color);
            if (endFound) {
                phonemes.Add(CreatePhoneme(endPhoneme, endStart, 2));
            } else {
                var (endPhoneme2, _, _) = GetMappedPhoneme($"{phonemeBase} R", tone, color);
                phonemes.Add(CreatePhoneme(endPhoneme2, endStart, 2));
            }
        }

        private void AddTransitionToNext(string fromPhoneme, Note nextNote, int tone, string color, int totalDuration, double? stretchRatio, List<Phoneme> phonemes) {
            string nextLyric = nextNote.lyric;
            string nextFirstPhoneme = GetFirstPhoneme(nextLyric);
            int nextFirstPhonemeDuration = GetFirstPhonemeDuration(nextLyric, nextNote.tone, stretchRatio);
            int stretchEnd = totalDuration - Math.Min(totalDuration / 2, nextFirstPhonemeDuration);
            var (phoneme, _, _) = GetMappedPhoneme($"{fromPhoneme} {nextFirstPhoneme}", tone, color);
            phonemes.Add(CreatePhoneme(phoneme, stretchEnd, 2));
        }

        private string GetFirstPhoneme(string lyric) {
            if (vowelDict.TryGetValue(lyric, out var vowel)) {
                return vowel;
            } else if (phonemeDict.TryGetValue(lyric, out var phonemes)) {
                return phonemes[0];
            }
            return lyric;
        }

        private string GetLastPhoneme(string lyric) {
            if (lyric.StartsWith("_")) {
                lyric = lyric.Substring(1);
            }
            if (vowelDict.TryGetValue(lyric, out var vowel)) {
                return vowel;
            } else if (phonemeDict.TryGetValue(lyric, out var phonemes)) {
                return phonemes[2];
            }
            return lyric;
        }

        private int GetFirstPhonemeDuration(string phoneme, int tone, double? stretchRatio = null) {
            if (singer.TryGetMappedOto(phoneme, tone, "", out var oto)) {
                int baseDuration = timeAxis.MsToTickAt(oto.Preutter * (stretchRatio ?? 1), 0);
                return baseDuration;
            }
            return Convert.ToInt32(30 * (stretchRatio ?? 1));
        }

        private int GetLastPhonemeDuration(string phoneme, int tone, double? stretchRatio = null) {
            if (singer.TryGetMappedOto(phoneme, tone, "", out var oto)) {
                int duration = timeAxis.MsToTickAt(oto.Overlap, 0);
                return Convert.ToInt32(duration * (stretchRatio ?? 1));
            }
            return Convert.ToInt32(30 * (stretchRatio ?? 1));
        }

        private int GetVowelDuration(string vowel, int tone, double? stretchRatio = null) {
            if (singer.TryGetMappedOto(vowel, tone, "", out var oto)) {
                int baseDuration = timeAxis.MsToTickAt(oto.Consonant - oto.Preutter + 20, 0);
                return Convert.ToInt32(baseDuration * (stretchRatio ?? 1));
            }
            return Convert.ToInt32(0 * (stretchRatio ?? 1));
        }
    }
}
