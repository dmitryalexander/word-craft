using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;

// App-local policy; shared render sources are adapted only in this app's build stage.
sealed class WordCraftQuality
{
    internal static readonly string[] Names={"Low","Balanced","High","Ultra"};
    static int selected=1;
    static readonly int[] dyeRows={540,720,1080,2160},flowRows={144,288,576,576},iterations={8,12,32,32},intervals={50,33,16,16};
    internal static int DyeRows {get{return dyeRows[selected];}}
    internal static int FlowRows {get{return flowRows[selected];}}
    internal static int Iterations {get{return iterations[selected];}}
    internal static int FrameMilliseconds {get{return intervals[selected];}}
    internal static bool Observe(float milliseconds){return false;}
    internal string FilePath=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"../Quality-Settings.json"));
    internal string Error {get;private set;}
    readonly Action changed;
    bool dirty;
    internal int Level {get{return selected;}set{int next=Math.Max(0,Math.Min(3,value));if(next==selected)return;selected=next;dirty=true;changed();}}
    internal WordCraftQuality(Action changed,string filePath=null)
    {
        this.changed=changed;if(filePath!=null)FilePath=filePath;selected=1;
        try{if(File.Exists(FilePath)){var data=new JavaScriptSerializer().Deserialize<Dictionary<string,int>>(File.ReadAllText(FilePath));int value;if(data!=null&&data.TryGetValue("quality",out value))selected=Math.Max(0,Math.Min(3,value));}}
        catch(Exception e){Error=e.Message;}
    }
    internal bool Save()
    {
        if(!dirty)return true;
        try{string temp=FilePath+".tmp";File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new {quality=selected}));if(File.Exists(FilePath))File.Replace(temp,FilePath,null);else File.Move(temp,FilePath);dirty=false;Error=null;return true;}
        catch(Exception e){Error=e.Message;return false;}
    }
}

sealed partial class CloudSmokeView
{
    internal void RefreshQuality(){smoke.RefreshQuality();}
}
