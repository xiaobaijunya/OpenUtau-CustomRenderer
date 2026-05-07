using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Classic;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core.Ustx;

#if DEBUG
namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("Presamp Sample Phonemizer", "ZH PRESAMP", language: "ZH")]
    public class PresampSamplePhonemizer : Phonemizer {
        // Supporting: [VOWEL][CONSONANT][PRIORITY][REPLACE][ALIAS(VCPAD,VCVPAD)]

        private USinger singer;
        private Presamp presamp;
        public override void SetSinger(USinger singer) {
            if (this.singer == singer) {
                return;
            }
            this.singer = singer;
            if (this.singer == null) {
                return;
            }

            presamp = new Presamp();
            presamp.SetVowels(defVowels);
            presamp.SetConsonants(defConsonants);
            presamp.Replace.Clear();
            presamp.Priorities = new List<string> { "k", "g", "t", "d", "b", "p" };
            presamp.AliasRules.ENDING1 = "_%v%"; // not supported yet
            presamp.AddEnding = 1; // not supported yet

            // Read ini after preparing default values for the phonemizer
            presamp.ReadPresampIni(singer.Location, singer.TextFileEncoding);
        }

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevNeighbours) {
            var lyric = notes[0].lyric;
            foreach (var pair in presamp.Replace) { // replace (exact match)
                if (pair.Key == lyric) {
                    lyric = pair.Value;
                }
            }

            string consonant = lyric;
            if (presamp.PhonemeList.TryGetValue(lyric, out PresampPhoneme currentPhoneme)) {
                consonant = currentPhoneme.Consonant;
            }
            string prevVowel = "-";
            if (prevNeighbour != null) {
                var prevLyric = prevNeighbour.Value.lyric;
                if (presamp.PhonemeList.TryGetValue(prevLyric, out PresampPhoneme prevPhoneme)) {
                    prevVowel = prevPhoneme.Vowel;
                }
            };
            string vcpad = presamp.AliasRules.VCPAD;

            var attr0 = notes[0].phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
            var attr1 = notes[0].phonemeAttributes?.FirstOrDefault(attr => attr.index == 1) ?? default;
            if (lyric == "-" || lyric.ToLowerInvariant() == "r") {
                if (singer.TryGetMappedOto($"{prevVowel}{vcpad}R", notes[0].tone + attr0.toneShift, attr0.voiceColor, out var oto1)) {
                    return MakeSimpleResult(oto1.Alias);
                }
                return MakeSimpleResult($"{prevVowel}{vcpad}R");
            }
            int totalDuration = notes.Sum(n => n.duration);
            if (singer.TryGetMappedOto($"{prevVowel}{vcpad}{lyric}", notes[0].tone + attr0.toneShift, attr0.voiceColor, out var oto)) {
                return MakeSimpleResult(oto.Alias);
            }
            int vcLen = 120;
            if (singer.TryGetMappedOto(lyric, notes[0].tone + attr0.toneShift, attr0.voiceColor, out var cvOto)) {
                if (cvOto.Overlap < 0) {
                    vcLen = MsToTick(cvOto.Preutter - cvOto.Overlap);
                } else {
                    vcLen = MsToTick(cvOto.Preutter);
                }
                vcLen = Convert.ToInt32(Math.Min(totalDuration / 2, vcLen * (attr0.consonantStretchRatio ?? 1)));
            }
            if (singer.TryGetMappedOto($"{prevVowel}{vcpad}{consonant}", notes[0].tone + attr0.toneShift, attr0.voiceColor, out oto)) {
                return new Result {
                    phonemes = new Phoneme[] {
                        new Phoneme() {
                            phoneme = oto.Alias,
                            position = - vcLen,
                        },
                        new Phoneme() {
                            phoneme = cvOto?.Alias ?? lyric,
                        },
                    },
                };
            }
            return MakeSimpleResult(cvOto?.Alias ?? lyric);
        }

        // Citation: https://delta-kimigatame.hatenablog.jp/entry/ar591802 CVVChinese用
        private readonly static List<string> defVowels = new List<string> {
            {"a=a=za,ca,sha,ta,lia,ka,cha,ga,kua,fa,hua,da,wa,pa,shua,zha,ma,ha,qia,jia,gua,zhua,xia,ba,dia,na,la,sa,ya,a=100"},
            {"ang=ang=nang,guang,cang,zhang,yang,niang,ang,dang,liang,shang,zhuang,kang,hang,tang,pang,chuang,huang,zang,wang,bang,jiang,gang,sang,kuang,rang,fang,chang,xiang,shuang,mang,qiang,lang=100"},
            {"ao=ao=gao,pao,yao,hao,diao,zao,qiao,shao,mao,cao,piao,xiao,zhao,tiao,lao,biao,sao,kao,nao,liao,dao,chao,jiao,miao,ao,tao,rao,niao,bao=100"},
            {"ai=ai=mai,lai,dai,pai,gai,chai,kuai,cai,bai,huai,shai,ai,chuai,tai,guai,zhuai,wai,hai,nai,shuai,zai,kai,zhai,sai=100"},
            {"an=an=duan,zan,shan,wan,ran,huan,guan,an,ruan,ban,chan,kuan,kan,tan,zhuan,han,can,nan,lan,dan,fan,pan,zhan,chuan,san,man,nuan,suan,shuan,zuan,luan,gan,tuan,cuan=100"},
            {"o=o=shuo,tuo,zhuo,ruo,bo,kuo,mo,fo,guo,duo,o,huo,suo,luo,zuo,po,cuo,wo,chuo,nuo=100"},
            {"ong=ong=qiong,ong,rong,tong,cong,xiong,dong,nong,jiong,yong,long,song,chong,gong,kong,hong,zhong,zong=100"},
            {"ou=ou=you,rou,qiu,dou,shou,diu,sou,mou,zou,niu,jiu,hou,miu,ou,kou,liu,cou,zhou,lou,chou,xiu,gou,fou,tou,pou=100"},
            {"e=e=se,le,che,ke,re,he,zhe,ge,she,ne,de,ce,ze,e,te,me=100"},
            {"en=en=cen,ken,ren,zhen,hen,sen,hun,zun,fen,pen,kun,zhun,lun,zen,sun,en,dun,nen,chen,ben,shun,run,shen,cun,tun,wen,chun,gen,gun,men=100"},
            {"eng=eng=teng,zeng,reng,weng,keng,seng,heng,geng,eng,cheng,sheng,neng,meng,zheng,beng,peng,leng,ceng,deng,feng=100"},
            {"ei=ei=sui,shei,nei,hei,pei,lei,zhei,tei,chui,tui,rui,zui,hui,zhui,mei,gui,kei,fei,gei,ei,wei,dei,cui,shui,zei,dui,bei,kui=100"},
            {"ie=ie=qie,nie,mie,ye,die,tie,lie,xie,pie,jie,bie=100"},
            {"ue=ue=nue,que,jue,yue,lue,xue=100"},
            {"u=u=wu,ru,ku,nu,lu,gu,bu,shu,zhu,chu,cu,pu,mu,zu,su,hu,tu,fu,u,du=100"},
            {"v=v=nv,lv,yu,qu,xu,ju=100"},
            {"vn=vn=jun,xun,qun,yun=100"},
            {"i=i=yi,bi,ti,ji,ni,xi,li,mi,pi,qi,di,i=100"},
            {"in=in=bin,jin,lin,qin,pin,yin,nin,min,xin=100"},
            {"ing=ing=jing,ding,ning,ting,qing,ping,xing,bing,ying,ming,ling=100"},
            {"ir=ir=ri,shi,zhi,chi=100"},
            {"iz=iz=si,zi,ci=100"},
            {"er=er=er=100"},
            {"ian=ian=quan,xuan,tian,pian,nian,yan,juan,bian,yuan,mian,dian,lian,qian,xian,jian=100"},
        };
        private readonly static List<string> defConsonants = new List<string> {
            { "ch=cha,chang,chao,chai,chan,chong,chou,che,chen,cheng,chi=1" },
            { "zh=zha,zhang,zhao,zhai,zhan,zhong,zhou,zhe,zhen,zheng,zhei,zhi=1" },
            { "sw=suan,suo,sun,sui,su=0" },
            { "xy=xia,xiang,xiao,xiong,xiu,xie,xi,xin,xing,xian=0" },
            { "zw=zuan,zuo,zun,zui,zu=1" },
            { "cw=cuan,cuo,cun,cui,cu=1" },
            { "xw=xue,xu,xun,xuan=0" },
            { "ny=niang,niao,niu,nie,ni,nin,ning,nian=0" },
            { "r=rang,rao,ran,ruan,ruo,rong,rou,re,ren,run,reng,rui,ru,ri=0" },
            { "zhw=zhua,zhuang,zhuai,zhuan,zhuo,zhun,zhui,zhu=1" },
            { "chw=chuang,chuai,chuan,chuo,chun,chui,chu=1" },
            { "ly=lia,liang,liao,liu,lie,li,lin,ling,lian=0" },
            { "jy=jia,jiang,jiao,jiong,jiu,jie,ji,jin,jing,jian=1" },
            { "jw=jue,ju,jun,juan=1" },
            { "hw=hua,huang,huai,huan,huo,hun,hui,hu=0" },
            { "qw=que,qu,qun,quan=1" },
            { "c=ca,cang,cao,cai,can,cong,cou,ce,cen,ceng,ci=1" },
            { "b=ba,bang,bao,biao,bai,ban,bo,ben,beng,bei,bie,bu,bi,bin,bing,bian=1" },
            { "d=da,dia,dang,dao,diao,dai,dan,duan,duo,dong,dou,diu,de,dun,deng,dei,dui,die,du,di,ding,dian=1" },
            { "g=ga,gua,gang,guang,gao,gai,guai,gan,guan,guo,gong,gou,ge,gen,gun,geng,gei,gui,gu=1" },
            { "f=fa,fang,fan,fo,fou,fen,feng,fei,fu=0" },
            { "qy=qia,qiang,qiao,qiong,qiu,qie,qi,qin,qing,qian=1" },
            { "h=ha,hang,hao,hai,han,hong,hou,he,hen,heng,hei=0" },
            { "k=ka,kua,kang,kuang,kao,kai,kuai,kan,kuan,kuo,kong,kou,ke,ken,kun,keng,kei,kui,ku=1" },
            { "shw=shua,shuang,shuai,shuan,shuo,shun,shui,shu=0" },
            { "m=ma,mang,mao,mai,man,mo,mou,me,men,meng,mei,mu=0" },
            { "l=la,lang,lao,lai,lan,luan,luo,long,lou,le,lun,leng,lei,lue,lu,lv=0" },
            { "n=na,nang,nao,nai,nan,nuan,nuo,nong,ne,nen,neng,nei,nue,nu,nv=0" },
            { "p=pa,pang,pao,piao,pai,pan,po,pou,pen,peng,pei,pie,pu,pi,pin,ping,pian=1" },
            { "s=sa,sang,sao,sai,san,song,sou,se,sen,seng,si=1" },
            { "sh=sha,shang,shao,shai,shan,shou,she,shen,sheng,shei,shi=1" },
            { "t=ta,tang,tao,tiao,tai,tan,tuan,tuo,tong,tou,te,tun,teng,tei,tui,tie,tu,ti,ting,tian=1" },
            { "w=wa,wang,wai,wan,wo,wen,weng,wei,wu=0" },
            { "v=yue,yu,yun,yuan=0" },
            { "y=ya,yang,yao,yong,you,ye,i,yi,yin,ying,yan=0" },
            { "z=za,zang,zao,zai,zan,zong,zou,ze,zen,zeng,zei,zi=1" },
            { "my=miao,miu,mie,mi,min,ming,mian=0" }
        };
        //private readonly static Dictionary<string, string> defReplace = new Dictionary<string, string>();
    }
}
#endif
