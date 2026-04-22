﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("Chinese VOCALOID Phonemizer", "ZH VOCALOID", language: "ZH")]
    public class ChineseVOCALOIDPhonemizer : Phonemizer {
        private Dictionary<string, string[]> phonemeDict = new Dictionary<string, string[]>();
        private Dictionary<string, string> vowelDict = new Dictionary<string, string>();
        private USinger singer;

        public override void SetSinger(USinger singer) {
            if (this.singer == singer) {
                return;
            }
            this.singer = singer;
            LoadDictionary();
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
                    string line;
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

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevNeighbours) {
            var note = notes[0];
            string lyric = note.lyric;
            var attr0 = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
            var attr1 = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == 1) ?? default;
            int tone = note.tone + attr0.toneShift;
            string color = attr0.voiceColor;

            if (lyric == "-") {
                if (prevNeighbour != null) {
                    var prevLyric = prevNeighbour.Value.lyric;
                    if (phonemeDict.TryGetValue(prevLyric, out var prevPhonemes)) {
                        var (phoneme, oto, found) = GetMappedPhoneme($"{prevPhonemes[2]} -", tone, color);
                        return MakeSimpleResult(phoneme);
                    } else if (vowelDict.TryGetValue(prevLyric, out var prevVowel)) {
                        var (phoneme, oto, found) = GetMappedPhoneme($"{prevVowel} -", tone, color);
                        return MakeSimpleResult(phoneme);
                    }
                }
                return MakeSimpleResult("-");
            }

            bool isFirstNote = prevNeighbour == null;
            bool isLastNote = nextNeighbour == null;
            bool nextNoteIsUnderscore = nextNeighbour != null && nextNeighbour.Value.lyric.StartsWith("_");
            
            string[] currentPhonemes;
            bool isVowelOnly = vowelDict.TryGetValue(lyric, out var vowel);

            if (isVowelOnly) {
                currentPhonemes = new string[] { vowel };
            } else if (!phonemeDict.TryGetValue(lyric, out currentPhonemes)) {
                var (phoneme, oto, found) = GetMappedPhoneme(lyric, tone, color);
                return MakeSimpleResult(phoneme);
            }
            
            var phonemes = new List<Phoneme>();
            int totalDuration = notes.Sum(n => n.duration);
            
            if (isVowelOnly) {
                if (isFirstNote) {
                    var (phoneme, oto, found) = GetMappedPhoneme($"- {vowel}", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = phoneme,
                        position = 0
                    });
                } else {
                    string prevLyric = prevNeighbour.Value.lyric;
                    string prevLastPhoneme = GetLastPhoneme(prevLyric);
                    var (phoneme, oto, found) = GetMappedPhoneme($"{prevLastPhoneme} {vowel}", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = phoneme,
                        position = 0
                    });
                }

                int vowelStartDuration = GetVowelDuration(vowel, tone, attr1.consonantStretchRatio);
                string stretchVowel = "_" + vowel;
                var (stretchPhonemeMapped, stretchOto, stretchFound) = GetMappedPhoneme(stretchVowel, tone, color);
                phonemes.Add(new Phoneme {
                    phoneme = stretchPhonemeMapped,
                    position = vowelStartDuration
                });

                if (isLastNote) {
                    int vowelDuration = GetVowelDuration(vowel, tone, attr1.consonantStretchRatio);
                    int endStart = totalDuration - Math.Min(totalDuration / 6, vowelDuration);
                    var (endPhoneme, endOto, endFound) = GetMappedPhoneme($"{vowel} -", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = endPhoneme,
                        position = endStart
                    });
                } else {
                    string nextLyric = nextNeighbour.Value.lyric;
                    string nextFirstPhoneme = GetFirstPhoneme(nextLyric);
                    int nextFirstPhonemeDuration = GetFirstPhonemeDuration(nextFirstPhoneme, nextNeighbour.Value.tone, attr1.consonantStretchRatio);
                    int stretchEnd = totalDuration - Math.Min(totalDuration / 2, nextFirstPhonemeDuration);
                    var (phoneme, oto, found) = GetMappedPhoneme($"{vowel} {nextFirstPhoneme}", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = phoneme,
                        position = stretchEnd
                    });
                }
            } else {
                string firstPhoneme = currentPhonemes[0];
                string stretchPhoneme = currentPhonemes[1];
                string lastPhoneme = currentPhonemes[2];

                int firstPhonemeDuration = GetFirstPhonemeDuration(firstPhoneme, tone, attr1.consonantStretchRatio);
                
                if (isFirstNote) {
                    var (startPhoneme, startOto, startFound) = GetMappedPhoneme($"- {firstPhoneme}", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = startPhoneme,
                        position = -firstPhonemeDuration
                    });
                }

                var (startPhoneme2, startOto2, startFound2) = GetMappedPhoneme(lyric, tone, color);
                phonemes.Add(new Phoneme {
                    phoneme = startPhoneme2,
                    position = 0
                });

                int stretchStart = GetStartPhonemeDuration(lyric, tone, attr1.consonantStretchRatio);
                var (stretchPhonemeMapped, stretchOto2, stretchFound2) = GetMappedPhoneme(stretchPhoneme, tone, color);
                phonemes.Add(new Phoneme {
                    phoneme = stretchPhonemeMapped,
                    position = stretchStart
                });

                if (!nextNoteIsUnderscore) {
                    bool nextIsVowel = nextNeighbour != null && vowelDict.ContainsKey(nextNeighbour.Value.lyric);
                    if (!nextIsVowel) {
                        if (isLastNote) {
                            int endDuration = GetLastPhonemeDuration(lastPhoneme, tone, attr1.consonantStretchRatio);
                            int endStart = totalDuration - Math.Min(totalDuration / 6, endDuration);
                            var (endPhoneme, endOto2, endFound2) = GetMappedPhoneme($"{lastPhoneme} -", tone, color);
                            phonemes.Add(new Phoneme {
                                phoneme = endPhoneme,
                                position = endStart
                            });
                        } else {
                            string nextLyric = nextNeighbour.Value.lyric;
                            string nextFirstPhoneme = GetFirstPhoneme(nextLyric);
                            int nextFirstPhonemeDuration = GetFirstPhonemeDuration(nextFirstPhoneme, nextNeighbour.Value.tone, attr1.consonantStretchRatio);
                            int stretchEnd = totalDuration - Math.Min(totalDuration / 2, nextFirstPhonemeDuration);
                            var (phoneme, oto, found) = GetMappedPhoneme($"{lastPhoneme} {nextFirstPhoneme}", tone, color);
                            phonemes.Add(new Phoneme {
                                phoneme = phoneme,
                                position = stretchEnd
                            });
                        }
                    }
                }
            }

            return new Result {
                phonemes = phonemes.ToArray()
            };
        }

        private (string phonemeName, UOto oto, bool found) GetMappedPhoneme(string phoneme, int tone, string color) {
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
                int baseDuration = timeAxis.MsToTickAt(oto.Consonant - oto.Preutter, 0);
                return Convert.ToInt32(baseDuration * (stretchRatio ?? 1));
            }
            return Convert.ToInt32(120 * (stretchRatio ?? 1));
        }

        private int GetStartPhonemeDuration(string phoneme, int tone, double? stretchRatio = null) {
            if (singer.TryGetMappedOto(phoneme, tone, "", out var oto)) {
                int overlapDuration = timeAxis.MsToTickAt(oto.Overlap, 0);
                int preutterDuration = timeAxis.MsToTickAt(oto.Preutter, 0);
                int duration = Math.Max(overlapDuration, preutterDuration);
                return Convert.ToInt32(duration * (stretchRatio ?? 1));
            }
            return Convert.ToInt32(120 * (stretchRatio ?? 1));
        }

        private int GetLastPhonemeDuration(string phoneme, int tone, double? stretchRatio = null) {
            if (singer.TryGetMappedOto(phoneme, tone, "", out var oto)) {
                int duration = timeAxis.MsToTickAt(oto.Overlap, 0);
                return Convert.ToInt32(duration * (stretchRatio ?? 1));
            }
            return Convert.ToInt32(120 * (stretchRatio ?? 1));
        }

        private int GetVowelDuration(string vowel, int tone, double? stretchRatio = null) {
            if (singer.TryGetMappedOto(vowel, tone, "", out var oto)) {
                int baseDuration = timeAxis.MsToTickAt(oto.Consonant - oto.Preutter, 0);
                return Convert.ToInt32(baseDuration * (stretchRatio ?? 1));
            }
            return Convert.ToInt32(120 * (stretchRatio ?? 1));
        }


        private bool HasOto(string alias, int tone) {
            return singer.TryGetMappedOto(alias, tone, out _);
        }
    }
}
