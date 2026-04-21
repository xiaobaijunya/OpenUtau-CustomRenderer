﻿using System;
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
            var attr = note.phonemeAttributes?.FirstOrDefault() ?? default;
            int tone = note.tone + attr.toneShift;
            string color = attr.voiceColor;

            if (lyric == "-") {
                if (prevNeighbour != null) {
                    var prevLyric = prevNeighbour.Value.lyric;
                    if (phonemeDict.TryGetValue(prevLyric, out var prevPhonemes)) {
                        string phoneme = GetMappedPhoneme($"{prevPhonemes[2]} -", tone, color);
                        return MakeSimpleResult(phoneme);
                    } else if (vowelDict.TryGetValue(prevLyric, out var prevVowel)) {
                        string phoneme = GetMappedPhoneme($"{prevVowel} -", tone, color);
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
                string phoneme = GetMappedPhoneme(lyric, tone, color);
                return MakeSimpleResult(phoneme);
            }

            var phonemes = new List<Phoneme>();
            int totalDuration = notes.Sum(n => n.duration);
            

            if (isVowelOnly) {
                if (isFirstNote) {
                    string phoneme = GetMappedPhoneme($"- {vowel}", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = phoneme,
                        position = 0
                    });
                } else {
                    string prevLyric = prevNeighbour.Value.lyric;
                    string prevLastPhoneme = GetLastPhoneme(prevLyric);
                    string phoneme = GetMappedPhoneme($"{prevLastPhoneme} {vowel}", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = phoneme,
                        position = 0
                    });
                }

                int vowelStartDuration = GetVowelDuration(vowel, tone);
                string stretchVowel = "_" + vowel;
                string stretchPhonemeMapped = GetMappedPhoneme(stretchVowel, tone, color);
                phonemes.Add(new Phoneme {
                    phoneme = stretchPhonemeMapped,
                    position = vowelStartDuration
                });

                if (isLastNote) {
                    int vowelDuration = GetVowelDuration(vowel, tone);
                    int endStart = totalDuration - vowelDuration;
                    string endPhoneme = GetMappedPhoneme($"{vowel} -", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = endPhoneme,
                        position = endStart
                    });
                } else {
                    string nextLyric = nextNeighbour.Value.lyric;
                    string nextFirstPhoneme = GetFirstPhoneme(nextLyric);
                    int nextFirstPhonemeDuration = GetFirstPhonemeDuration(nextFirstPhoneme, nextNeighbour.Value.tone);
                    int stretchEnd = totalDuration - nextFirstPhonemeDuration;
                    string phoneme = GetMappedPhoneme($"{vowel} {nextFirstPhoneme}", tone, color);
                    phonemes.Add(new Phoneme {
                        phoneme = phoneme,
                        position = stretchEnd
                    });
                }
            } else {
                string firstPhoneme = currentPhonemes[0];
                string stretchPhoneme = currentPhonemes[1];
                string lastPhoneme = currentPhonemes[2];

                int firstPhonemeDuration = GetFirstPhonemeDuration(firstPhoneme, tone);
                
                if (isFirstNote) {
                    string startPhoneme = GetMappedPhoneme($"- {firstPhoneme}", tone, color);
                    if (HasOto($"- {firstPhoneme}", tone)) {
                        phonemes.Add(new Phoneme {
                            phoneme = startPhoneme,
                            position = -firstPhonemeDuration
                        });
                    }
                }

                string startPhoneme2 = GetMappedPhoneme(lyric, tone, color);
                phonemes.Add(new Phoneme {
                    phoneme = startPhoneme2,
                    position = 0
                });

                int stretchStart = GetStartPhonemeDuration(lyric, tone);
                string stretchPhonemeMapped = GetMappedPhoneme(stretchPhoneme, tone, color);
                phonemes.Add(new Phoneme {
                    phoneme = stretchPhonemeMapped,
                    position = stretchStart
                });

                if (!nextNoteIsUnderscore) {
                    bool nextIsVowel = nextNeighbour != null && vowelDict.ContainsKey(nextNeighbour.Value.lyric);
                    if (!nextIsVowel) {
                        if (isLastNote) {
                            int endStart = totalDuration - GetLastPhonemeDuration(lastPhoneme, tone);
                            string endPhoneme = GetMappedPhoneme($"{lastPhoneme} -", tone, color);
                            phonemes.Add(new Phoneme {
                                phoneme = endPhoneme,
                                position = endStart
                            });
                        } else {
                            string nextLyric = nextNeighbour.Value.lyric;
                            string nextFirstPhoneme = GetFirstPhoneme(nextLyric);
                            int nextFirstPhonemeDuration = GetFirstPhonemeDuration(nextFirstPhoneme, nextNeighbour.Value.tone);
                            int stretchEnd = totalDuration - nextFirstPhonemeDuration;
                            string phoneme = GetMappedPhoneme($"{lastPhoneme} {nextFirstPhoneme}", tone, color);
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

        private string GetMappedPhoneme(string phoneme, int tone, string color) {
            if (singer.TryGetMappedOto(phoneme, tone, color, out var oto)) {
                return oto.Alias;
            }
            
            for (int t = 1; t <= 36; t++) {
                int tone1 = tone - t;
                int tone2 = tone + t;
                if (singer.TryGetMappedOto(phoneme, tone1, color, out var oto2)) {
                    return oto2.Alias;
                }
                if (singer.TryGetMappedOto(phoneme, tone2, color, out var oto3)) {
                    return oto3.Alias;
                }
            }
            
            return phoneme;
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

        private int GetFirstPhonemeDuration(string phoneme, int tone) {
            if (singer.TryGetMappedOto(phoneme, tone, "", out var oto)) {
                return timeAxis.MsToTickAt(oto.Preutter, 0);
            }
            return 120;
        }

        private int GetStartPhonemeDuration(string phoneme, int tone) {
            if (singer.TryGetMappedOto(phoneme, tone, "", out var oto)) {
                return timeAxis.MsToTickAt(oto.Overlap, 0);
            }
            return 120;
        }

        private int GetLastPhonemeDuration(string phoneme, int tone) {
            if (singer.TryGetMappedOto(phoneme, tone, "", out var oto)) {
                return timeAxis.MsToTickAt(oto.Overlap, 0);
            }
            return 120;
        }

        private int GetVowelDuration(string vowel, int tone) {
            if (singer.TryGetMappedOto(vowel, tone, "", out var oto)) {
                return timeAxis.MsToTickAt(oto.Overlap, 0);
            }
            return 120;
        }

        private bool HasOto(string alias, int tone) {
            return singer.TryGetMappedOto(alias, tone, out _);
        }
    }
}
