using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// Audio projection only. Displayed and sent Japanese text is never rewritten.
sealed class JapaneseRomaji : IDisposable
{
    static readonly Dictionary<string,string> kana = MakeKana();
    readonly Dictionary<string,string[]> plans = new Dictionary<string,string[]>();
    object imeObject;
    IFeLanguage ime;
    bool triedIme;

    [ComImport, Guid("019F7152-E6DB-11D0-83C3-00C04FDDB82E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFeLanguage
    {
        [PreserveSig] int Open();
        [PreserveSig] int Close();
        [PreserveSig] int GetJMorphResult(uint request,uint mode,int length,
            [MarshalAs(UnmanagedType.LPWStr)]string text,IntPtr info,out IntPtr result);
        [PreserveSig] int GetConversionModeCaps(out uint caps);
        [PreserveSig] int GetPhonetic([MarshalAs(UnmanagedType.BStr)]string text,int start,int length,
            [MarshalAs(UnmanagedType.BStr)]out string result);
        [PreserveSig] int GetConversion([MarshalAs(UnmanagedType.BStr)]string text,int start,int length,
            [MarshalAs(UnmanagedType.BStr)]out string result);
    }

    static Dictionary<string,string> MakeKana()
    {
        var map=new Dictionary<string,string>();
        string[] rows={
            "あいうえお|a i u e o", "かきくけこ|ka ki ku ke ko", "がぎぐげご|ga gi gu ge go",
            "さしすせそ|sa shi su se so", "ざじずぜぞ|za ji zu ze zo", "たちつてと|ta chi tsu te to",
            "だぢづでど|da ji zu de do", "なにぬねの|na ni nu ne no", "はひふへほ|ha hi fu he ho",
            "ばびぶべぼ|ba bi bu be bo", "ぱぴぷぺぽ|pa pi pu pe po", "まみむめも|ma mi mu me mo",
            "やゆよ|ya yu yo", "らりるれろ|ra ri ru re ro", "わゐゑをん|wa wi we wo n",
            "ぁぃぅぇぉ|a i u e o", "ゃゅょゎゕゖゔ|ya yu yo wa ka ke vu"
        };
        foreach(string row in rows)
        {
            var parts=row.Split('|');var sounds=parts[1].Split(' ');
            for(int i=0;i<parts[0].Length;i++)map[parts[0][i].ToString()]=sounds[i];
        }
        string bases="きぎしじちぢにひびぴみり";
        string[] prefixes="ky gy sh j ch j ny hy by py my ry".Split(' ');
        for(int i=0;i<bases.Length;i++)for(int j=0;j<3;j++)
            map[bases[i].ToString()+"ゃゅょ"[j]]=prefixes[i]+"auo"[j];
        foreach(string entry in ("いぇ:ye しぇ:she じぇ:je ちぇ:che てぃ:ti でぃ:di とぅ:tu どぅ:du " +
            "てゅ:tyu でゅ:dyu ふぁ:fa ふぃ:fi ふぇ:fe ふぉ:fo ふゅ:fyu ふゃ:fya ふょ:fyo " +
            "ゔぁ:va ゔぃ:vi ゔぇ:ve ゔぉ:vo ゔゅ:vyu うぃ:wi うぇ:we うぉ:wo " +
            "つぁ:tsa つぃ:tsi つぇ:tse つぉ:tso くぁ:kwa くぃ:kwi くぇ:kwe くぉ:kwo " +
            "ぐぁ:gwa ぐぃ:gwi ぐぇ:gwe ぐぉ:gwo すぃ:si ずぃ:zi").Split(' '))
        {
            var pair=entry.Split(':');map[pair[0]]=pair[1];
        }
        return map;
    }

    internal static bool ContainsJapanese(string text)
    {
        foreach(char c in text)
            if((c>=0x3040&&c<=0x30ff)||(c>=0x3400&&c<=0x9fff)||
                (c>=0xf900&&c<=0xfaff)||(c>=0xff66&&c<=0xff9f)||c==0x3005)return true;
        return false;
    }

    static bool NeedsReading(string text)
    {
        foreach(char c in text)if((c>=0x3400&&c<=0x9fff)||(c>=0xf900&&c<=0xfaff)||
            c==0x3005||c==0x309d||c==0x309e||c==0x30fd||c==0x30fe)return true;
        return false;
    }

    string Reading(string text)
    {
        if(!NeedsReading(text))return text;
        try
        {
            if(!triedIme)
            {
                triedIme=true;
                Type type=Type.GetTypeFromProgID("MSIME.Japan");
                if(type!=null)
                {
                    imeObject=Activator.CreateInstance(type);ime=imeObject as IFeLanguage;
                    if(ime==null || ime.Open()<0)DisposeIme();
                }
            }
            string reading;
            if(ime!=null && ime.GetPhonetic(text,1,-1,out reading)>=0 && !String.IsNullOrEmpty(reading))
                return reading;
        }
        catch(COMException) { DisposeIme(); }
        catch(InvalidCastException) { DisposeIme(); }
        return text;
    }

    static string NormalizeKana(string text)
    {
        var s=new StringBuilder(text.Normalize(NormalizationForm.FormKC));
        for(int i=0;i<s.Length;i++)if(s[i]>=0x30a1&&s[i]<=0x30f6)s[i]=(char)(s[i]-0x60);
        return s.ToString();
    }

    static string Syllable(string text,int index,out int consumed)
    {
        string result;consumed=1;
        if(index+1<text.Length && kana.TryGetValue(text.Substring(index,2),out result)){consumed=2;return result;}
        if(kana.TryGetValue(text[index].ToString(),out result))return result;
        switch(text[index])
        {
            case '。':return ".";case '、':return ",";case '・':return " ";
            case '「':case '」':case '『':case '』':return "\"";
            case 'ヷ':return "va";case 'ヸ':return "vi";case 'ヹ':return "ve";case 'ヺ':return "vo";
        }
        return text[index].ToString();
    }

    static string RomanizeKana(string text,out List<int> owners)
    {
        text=NormalizeKana(text);owners=new List<int>();var result=new StringBuilder();
        for(int i=0;i<text.Length;)
        {
            int used=1;string syllable;
            if(text[i]=='っ')
            {
                int nextUsed;string next=i+1<text.Length?Syllable(text,i+1,out nextUsed):"";
                syllable=next.StartsWith("ch",StringComparison.Ordinal)?"t":
                    next.Length>0&&"bcdfghjklmnpqrstvwxyz".IndexOf(next[0])>=0?next[0].ToString():"tsu";
            }
            else if(text[i]=='ー')
            {
                syllable="";
                for(int j=result.Length-1;j>=0;j--)if("aeiou".IndexOf(result[j])>=0){syllable=result[j].ToString();break;}
            }
            else syllable=Syllable(text,i,out used);
            result.Append(syllable);
            for(int j=0;j<syllable.Length;j++)owners.Add(i);
            i+=used;
        }
        return result.ToString().ToUpperInvariant();
    }

    internal string Romanize(string text)
    {
        List<int> owners;return RomanizeKana(Reading(text??""),out owners);
    }

    internal string At(string word,int sourceOffset)
    {
        if(String.IsNullOrEmpty(word)||sourceOffset<0||sourceOffset>=word.Length)return "";
        string[] plan;
        if(!plans.TryGetValue(word,out plan))
        {
            string reading=Reading(word);List<int> owners;
            string romaji=RomanizeKana(reading,out owners);
            plan=new string[word.Length];for(int i=0;i<plan.Length;i++)plan[i]="";
            if(reading==word && word.IsNormalized(NormalizationForm.FormKC))
            {
                for(int i=0;i<romaji.Length;i++)if(owners[i]<plan.Length)plan[owners[i]]+=romaji[i];
            }
            else
            {
                // Kanji readings are contextual whole-word results. Distribute their
                // letters over source graphemes without guessing a reading per kanji.
                int[] starts=StringInfo.ParseCombiningCharacters(word);int before=0;
                for(int i=0;i<starts.Length;i++)
                {
                    int end=(int)Math.Round((i+1)*romaji.Length/(double)starts.Length);
                    plan[starts[i]]=romaji.Substring(before,end-before);before=end;
                }
            }
            if(plans.Count>=128)plans.Clear();plans[word]=plan;
        }
        return plan[sourceOffset];
    }

    void DisposeIme()
    {
        if(ime!=null){try{ime.Close();}catch(COMException){}ime=null;}
        if(imeObject!=null){Marshal.FinalReleaseComObject(imeObject);imeObject=null;}
    }
    public void Dispose(){DisposeIme();plans.Clear();}
}
