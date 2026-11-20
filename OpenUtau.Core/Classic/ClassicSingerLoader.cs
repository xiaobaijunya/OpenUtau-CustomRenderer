using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Classic {
    public static class ClassicSingerLoader {
        static USinger AdjustSingerType(Voicebank v) {
            switch (v.SingerType) {
                case USingerType.Enunu:
                    return new Core.Enunu.EnunuSinger(v) as USinger;
                case USingerType.DiffSinger:
                    return new Core.DiffSinger.DiffSingerSinger(v) as USinger;
                case USingerType.Voicevox:
                    return new Core.Voicevox.VoicevoxSinger(v) as USinger;
                default:
                    return new ClassicSinger(v) as USinger;
            }
        }
        public static IEnumerable<USinger> FindAllSingers() {
            List<USinger> singers = new List<USinger>();
            // 声库的 Id 是相对各自声库根目录的路径，所以不同根目录下的同名文件夹会得到
            // 相同的 Id，后加载的那一个会被当成重复项忽略（见 SingerManager.SearchAllSingers）。
            // 这里给撞 Id 的声库加上“根目录名”后缀，让两个声库都能加载，
            // 例如 E:\UTAU\voice\点萤UTAU声库 -> Id "点萤UTAU声库 (voice)"。
            var usedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in PathManager.Inst.SingersPaths) {
                var loader = new VoicebankLoader(path);
                foreach (var voicebank in loader.SearchAll()) {
                    string location = Path.GetDirectoryName(voicebank.File) ?? string.Empty;
                    string id = voicebank.Id ?? string.Empty;
                    if (usedIds.TryGetValue(id, out var usedLocation)
                        && !string.Equals(usedLocation, location, StringComparison.OrdinalIgnoreCase)) {
                        id = MakeUniqueId(id, path, usedIds);
                        voicebank.Id = id;
                    }
                    usedIds[id] = location;
                    singers.Add(AdjustSingerType(voicebank));
                }
            }
            return singers;
        }

        /// <summary>
        /// 为同名不同路径的声库生成唯一 Id：优先用声库根目录名做后缀，冲突时再加序号。
        /// </summary>
        static string MakeUniqueId(string id, string singersRoot, Dictionary<string, string> usedIds) {
            string rootName = Path.GetFileName(
                singersRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(rootName)) {
                rootName = singersRoot;
            }
            string candidate = $"{id} ({rootName})";
            int n = 2;
            while (usedIds.ContainsKey(candidate)) {
                candidate = $"{id} ({rootName} {n++})";
            }
            return candidate;
        }
    }
}
