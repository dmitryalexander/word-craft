using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// Continuous letter-paced ribbon with a spatial per-letter magnification wave.
sealed class KineticReader : Control {
 public readonly FlowTuning Tuning=new FlowTuning();
 public static readonly Color Paper=Color.Black,Ink=Color.FromArgb(218,218,211),FocusInk=Color.FromArgb(255,242,210);
 static readonly Color ReadyInk=Color.FromArgb(126,218,147),ReadReadyInk=Color.FromArgb(99,170,116),ReadyFocusInk=Color.FromArgb(197,255,192);
 static readonly Color LoadingInk=Color.FromArgb(239,151,77),LoadingFocusInk=Color.FromArgb(255,187,116);

 sealed class Part {public float LastX,LastWidth,LastHeight,LastEmphasis;public string Text;public FontStyle Style;public int Source;public float X,Width,FocusX,FocusWidth,Flow,Target,Velocity,Emphasis,LayoutX;public int Role=2;}
 sealed class Word {public bool SmokeSeen,SmokeReleased;public float SmokeScale=1;public string Text;public int Start,End;public RectangleF Bounds;public float FocusWidth,Tape,Lava=1;public readonly List<Part> Parts=new List<Part>();}
 struct PaintState {
  public float CenterX,CenterY,Width,RowStep,Radius,RadiusX,RadiusY;
  public int Force,Rotation,RotationResponse,FocusRow;
  public bool HeatTint;
  public Color ReadInk, UnreadInk;
 }
 [StructLayout(LayoutKind.Sequential)] struct Range {public int Start,End;}
 [StructLayout(LayoutKind.Sequential)] struct TextRange {public Range Range;public IntPtr Text;}
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,ref TextRange l);

 internal bool DissolvingLine { get; set; }
 internal bool SeparateSmoke { get; set; }
 Color singleLineInk=Color.White;
 internal Color SingleLineInk { get { return singleLineInk; } set { if(singleLineInk!=value){singleLineInk=value;rasterDirty=true;} } }
 internal float SmokeFade { get; set; }
 internal struct SmokeHandoff { internal Bitmap Mask; internal float X,Y,Velocity,Release;internal int Words; }
 internal event Action<SmokeHandoff> SmokeReleased;
 readonly ReaderRaster smokeRaster=new ReaderRaster();
 readonly Random smokeSizes=new Random();
 float smokeWeight,smokeArea,smokeX,smokeY,smokeVelocity,rasterSmokeFade;
 int smokeWords;bool smokeRasterOpen;
 readonly ReaderRaster raster=new ReaderRaster();bool rasterDirty=true;bool flowMoving=true;
 Color rasterReadInk, rasterUnreadInk;
 public double BlitPaintMs{get;private set;}public double CompositePaintMs{get;private set;}
 readonly Dictionary<string,SizeF> letterWidths=new Dictionary<string,SizeF>();
 static readonly float[] RingScales = { 1f, .72f, .44f };
 readonly System.Drawing.Drawing2D.GraphicsPath[] ringPaths = new System.Drawing.Drawing2D.GraphicsPath[RingScales.Length];
 int cachedCircleRadius;
 float ringSizeScale = 1f;
 public float OuterRingRadius { get { return Tuning.Get("circleRadius") * ringSizeScale; } }
 public bool IsSizingEffects { get; private set; }
 public event Action EffectSizeCommitted;
 int sizingCircleRadius, sizingLavaRadius, sizingPreviousX;
 double sizingLogScale;
 public int MeasuredLetters{get{return letterWidths.Count;}}
 public int PaintedWords{get;private set;}
 public int PaintedLetters{get;private set;}
 readonly List<Word> words=new List<Word>();
 readonly Timer timer=new Timer{Interval=15};
 readonly Stopwatch clock=new Stopwatch();
 readonly Stopwatch playbackClock=new Stopwatch();
 int playbackDirection,lettersPerSecond=FlowTuning.ReadingSpeed.Default,focusPartIndex,currentIndex;
 double nextWordTime,playbackSeconds;
 ReaderNarration narration;
 bool narrationEnabled,playWhenPrepared,resumeOnClip,prepareAfterText;
 double narrationPhase;
 int narrationEnd=-1;
 int lastPrefetchEnd=-1;
 public Func<bool> RequestMoreNarrationText;
 public string NarrationSource { get; set; }
 public bool NarrationEnabled{get{return narrationEnabled;}}
 public bool NarrationPreparing{get{return prepareAfterText||narration!=null&&narration.Preparing;}}
 public string NarrationStatus{get{return narration==null?null:narration.Status;}}
 public event Action NarrationChanged;
 public int PlaybackDirection{get{return playbackDirection;}}
 public int LettersPerSecond{get{return lettersPerSecond;}}
 bool slowPlayback;
 internal double SlowPlaybackFactor = .67;
 internal Func<bool> ReadSlowPlaybackModifier { get; set; }
 internal Func<int,FontStyle> ReadGlyphStyle { get; set; }
 public double EffectiveLettersPerSecond{get{return lettersPerSecond*(slowPlayback?SlowPlaybackFactor:1);}}
 public void SetPlaybackSlow(bool held,double? atSeconds=null){
  ApplyPlaybackSlow(held,atSeconds??playbackClock.Elapsed.TotalSeconds,false);
 }
 void PollPlaybackModifier(double now){
  if(ReadSlowPlaybackModifier==null)return;
  // After a stalled tick we cannot tell when the key changed. Resume the last
  // displayed phase at the current rate instead of catching up at a stale rate.
  const double stalledIntervalSeconds=.1;
  ApplyPlaybackSlow(ReadSlowPlaybackModifier(),now,now-playbackSeconds>stalledIntervalSeconds);
 }
 void ApplyPlaybackSlow(bool held,double now,bool skipStalledInterval){
  if(slowPlayback==held)return;
  if(narrationEnabled&&playbackDirection>=0){slowPlayback=held;return;}
  double phase=0;
  if(playbackDirection!=0){
   if(!skipStalledInterval)AdvancePlayback(now);
   double phaseTime=skipStalledInterval?playbackSeconds:now;
   double interval=1.0/EffectiveLettersPerSecond;
   phase=Math.Max(0,Math.Min(1,(phaseTime-nextWordTime+interval)/interval));
  }
  slowPlayback=held;
  if(playbackDirection!=0){
   nextWordTime=now+(1-phase)/EffectiveLettersPerSecond;
   playbackSeconds=now;
   LayoutFlow();flowMoving=true;Wake();
  }
  if(PlaybackChanged!=null)PlaybackChanged();
 }
 public event Action PlaybackChanged;
 RuledReader source;Font readingFont,focusFont;
 public string FontFamilyName { get; private set; }
 public const string DefaultFontFamily = "Scheherazade";
 public void ResetFontFamily(){if(!SetFontFamily(DefaultFontFamily))SetFontFamily("Georgia");}
 public bool SetFontFamily(string name) {
  try {
   using (var family = new FontFamily(name)) {
    if (!family.IsStyleAvailable(FontStyle.Regular)) return false;
    if (FontFamilyName == family.Name) return true;
    FontFamilyName = family.Name;
   }
  } catch (ArgumentException) { return false; }
  if (source != null && current != null) BuildPage(pageStart, TextAnchor);
  return true;
 }
 Word current;
 int pageStart,pageEnd,knownLength,paintCount,cachedReadEnd;
 bool active=true;
 double previousTick;

 float focusHeight,glyphHeight,fieldWidth;
 double circleAngle;
 public double CircleAngle{get{return circleAngle;}}
 public void SetTuning(string key,double value){
  int previousBudget=key=="lineSpacing"&&current!=null?ContextBudget:0;
  Tuning.Set(key,value);
  if(key.StartsWith("narration",StringComparison.Ordinal)){
   if(narration!=null)narration.Configure(Tuning.Get("narrationBass"),Tuning.Get("narrationMids"),Tuning.Get("narrationTreble"),
    Tuning.Get("narrationComma"),Tuning.Get("narrationSentence"));
   return;
  }
  rasterDirty=true;flowMoving=true;
  if(key=="peakPercent"&&source!=null)BuildPage(pageStart,TextAnchor);
  else if(previousBudget>0&&ContextBudget>previousBudget)BuildPage(pageStart,TextAnchor,true);
  Wake();Invalidate();
 }
 public float FocusScale{get{return current==null?1:1+(Tuning.Get("peakPercent")/100f-1)*current.Parts[focusPartIndex].Emphasis;}}
 public float FocusSizeRatio{get{return readingFont==null?1:focusFont.Size/readingFont.Size;}}
 public int VisibleCacheDrops{get;private set;}
 public int FocusWrappedLetters{get{int n=0;if(current!=null)foreach(var p in current.Parts)if(RibbonRow(p.Target,0)!=0)n++;return n;}}
 public float FocusLetterFlow{get{return current==null?0:current.Parts[focusPartIndex].Flow;}}
 internal struct LineWake
 {
  internal float X, Y, Speed, Direction;
  internal uint Priority;
 }

 // Follow one moving letter on each ribbon row. Source offsets keep the choice attached
 // to the same word across frames, even as new words enter at the edge.
 internal void SampleLineWakes(List<LineWake> rows)
 {
  rows.Clear();
  if(current==null || Height<1 || Width<1)return;
  var paint=CreatePaintState();
  float spacing=Math.Max(1,paint.RowStep);
  int firstRow=(int)Math.Ceiling((-paint.CenterY-paint.Force)/spacing);
  int lastRow=(int)Math.Floor((Height-paint.CenterY+paint.Force)/spacing);
  for(int row=firstRow;row<=lastRow;row++)rows.Add(new LineWake());
  foreach(var word in words)
  {
   foreach(var part in word.Parts)
   {
    if(Math.Abs(part.Velocity)<Width*.005f)continue;
    float partWidth=PartWidth(word,part);
    float raw=DissolvingLine?SingleLineX(word,part,partWidth):paint.CenterX+part.Flow+partWidth*.5f;
    int row=DissolvingLine?0:(int)Math.Floor(raw/paint.Width);
    if(row<firstRow || row>lastRow)continue;
    uint priority=unchecked((uint)(word.Start+part.Source+1)*2654435761u);
    int index=row-firstRow;
    if(priority<=rows[index].Priority)continue;
    float x=raw-row*paint.Width, y=paint.CenterY+row*spacing;
    float lava=DissolvingLine||word==current?0:word.Lava;
    var push=Spread(x,y,ref paint);
    var ahead=Spread(x+2,y,ref paint);
    float px=(x+lava*push.X)/Width, py=(y+lava*push.Y)/Height;
    if(px<.025f || px>.975f || py<.025f || py>.975f)continue;
    float dx=1+lava*(ahead.X-push.X)*.5f, dy=lava*(ahead.Y-push.Y)*.5f;
    float speed=part.Velocity/Width*(float)Math.Sqrt(dx*dx+dy*dy);
    rows[index]=new LineWake { X=px, Y=py, Speed=speed, Direction=(float)Math.Atan2(dy,dx), Priority=priority };
   }
  }
 }
 public float FocusLetterWidth{get{return current==null?0:PartWidth(current,current.Parts[focusPartIndex]);}}
 public float FocusLetterTarget{get{return current==null?0:current.Parts[focusPartIndex].Target;}}
 public Dictionary<int,float[]> CaptureFlowState(){var result=new Dictionary<int,float[]>();foreach(var w in words)foreach(var p in w.Parts)result[w.Start+p.Source]=new[]{p.Flow,PartWidth(w,p),PartHeight(p),p.Target,p.Emphasis};return result;}
 public double WordSpacingError{get{double error=0;foreach(var w in words)if(w.Parts.Count>0)foreach(var p in w.Parts)error=Math.Max(error,Math.Abs(p.Flow-w.Parts[0].Flow-p.LayoutX));return error;}}
 public int CachedWords{get{return words.Count;}}
 public int ContextCharacters{get;private set;}
 float RibbonWidth{get{return Math.Max(80,Width);}}
 public float RowStep{get{return Math.Max(glyphHeight*1.5f,focusHeight)*Tuning.Get("lineSpacing")/100f;}}
 int RibbonRow(float flow,int role){return (int)Math.Floor((ReadingPoint.X+flow)/RibbonWidth);}
 public int AheadCharactersNeeded{get{return Math.Max(1500,ContextBudget);}}
 int ContextBudget{get{return Math.Min(24000,Math.Max(2048,(int)(RibbonWidth*(Math.Ceiling(Height/Math.Max(1,RowStep)/2)+5)/Math.Max(5,glyphHeight*.26f))));}}
 public int LineCount{get{var rows=new HashSet<int>();if(current!=null)rows.Add(0);foreach(var word in words)if(word!=current)foreach(var part in word.Parts)rows.Add(RibbonRow(part.Target,part.Role));return rows.Count;}}
 public bool FlowOrderCorrect{get{float last=float.MinValue;foreach(var word in words)foreach(var part in word.Parts){if(part.Target<last-.01)return false;last=part.Target;}return true;}}
 public int SplitWords{get{int count=0;foreach(var word in words){if(word==current||word.Parts.Count<2)continue;int first=RibbonRow(word.Parts[0].Target,word.Parts[0].Role);foreach(var part in word.Parts)if(RibbonRow(part.Target,part.Role)!=first){count++;break;}}return count;}}
 public int MovingLetters{get{int count=0;foreach(var word in words)if(word!=current)foreach(var part in word.Parts)if(Math.Abs(part.Target-part.Flow)>.02)count++;return count;}}
 public int EdgeFragments{get{int count=0;foreach(var word in words)if(word!=current)foreach(var part in word.Parts){int row=RibbonRow(part.Flow,part.Role);float x=ReadingPoint.X+part.Flow-(float)Math.Floor((ReadingPoint.X+part.Flow)/RibbonWidth)*RibbonWidth;if(x+part.Width+2>RibbonWidth)count++;}return count;}}
 public PointF ReadingPoint{get{return new PointF(Width*.5f,Height*.5f);}}
 public RectangleF FocusScreenBounds{get{return current==null?RectangleF.Empty:new RectangleF(ReadingPoint.X-PartWidth(current,current.Parts[focusPartIndex])/2,ReadingPoint.Y-PartHeight(current.Parts[focusPartIndex])/2,PartWidth(current,current.Parts[focusPartIndex]),PartHeight(current.Parts[focusPartIndex]));}}
 public double GroupDistance{get{double distance=0;int count=0;foreach(var word in words)if(word!=current)foreach(var part in word.Parts){distance+=Math.Abs(part.Target-part.Flow);count++;}return count==0?0:distance/count;}}
 public int TextAnchor{get{return current==null?pageStart:current.Start+current.Parts[focusPartIndex].Source;}}
 internal string FocusedGrapheme { get { return current == null ? "" : current.Parts[focusPartIndex].Text; } }
 internal string FocusedWordText { get { return current == null ? "" : current.Text; } }
 internal string FocusedWordPrefix { get { return current == null ? "" : current.Text.Substring(0,Math.Min(current.Text.Length,current.Parts[focusPartIndex].Source+current.Parts[focusPartIndex].Text.Length)); } }
 internal int FocusedWordStart { get { return current == null ? -1 : current.Start; } }
 internal bool AtBufferedEnd { get { return current != null && focusPartIndex == current.Parts.Count - 1 &&
  NextStart(current.End) < 0; } }
 public int PageStart{get{return pageStart;}}
 public int PageEnd{get{return pageEnd;}}
 public int GlyphCount{get{int count=0;foreach(var word in words)count+=word.Text.Length;return count;}}
 public int NativePaints{get{return paintCount;}}
 public string CurrentWord{get{return current==null?"":current.Text;}}
 internal float? ZoomOverride { get; set; }
 public float Zoom{get{return ZoomOverride??(source==null?1:source.ZoomFactor);}}
 public bool Animating{get{return timer.Enabled;}}
 public double Displacement{get{var paint=CreatePaintState();double result=0;foreach(var word in words)if(word!=current)foreach(var part in word.Parts){int row=RibbonRow(part.Flow,part.Role);float x=ReadingPoint.X+part.Flow-(float)Math.Floor((ReadingPoint.X+part.Flow)/RibbonWidth)*RibbonWidth;var push=Spread(x+part.Width/2,ReadingPoint.Y+row*RowStep,ref paint);result+=Math.Abs(push.X)+Math.Abs(push.Y);}return result;}}
 public double FocusDisplacement{get{return 0;}}
 public event Action<int> Navigated;
 public event Action<float> ZoomRequested;
 internal Action<int,bool> WheelOverride;
 internal bool CenterWholeWord { get; set; }
 internal bool ReadingBloom { get; set; }
 internal Func<double,SizeF?> FocusPopCurve { get; set; }
 SizeF focusPop=new SizeF(1,1);
 double focusPopElapsed;
 Word focusPopWord;
 internal void PlayFocusPop(){
  if(current==null||FocusPopCurve==null)return;
  focusPopWord=current;focusPopElapsed=0;focusPop=FocusPopCurve(0).GetValueOrDefault(new SizeF(1,1));
  rasterDirty=true;Wake();Invalidate();
 }
 bool AdvanceFocusPop(double seconds){
  if(focusPopWord==null)return false;
  focusPopElapsed+=Math.Max(0,seconds);
  SizeF? scale=focusPopWord==current?FocusPopCurve(focusPopElapsed):null;
  focusPop=scale.GetValueOrDefault(new SizeF(1,1));
  if(!scale.HasValue)focusPopWord=null;
  rasterDirty=true;
  return true;
 }
 internal bool TrackWordHits;
 int selectionStart, selectionEnd;
 static readonly Color SelectionInk = Color.FromArgb(85,165,255);
 internal void SetTextSelection(int start,int end){
  if(selectionStart==start&&selectionEnd==end)return;
  selectionStart=start;selectionEnd=end;rasterDirty=true;Invalidate();
 }
 readonly List<WordHit> wordHits=new List<WordHit>();
 struct WordHit {
  public int Start;
  public float X,Y,Width,Height,Angle;
 }
 internal int WordAtPoint(Point point){
  for(int i=wordHits.Count-1;i>=0;i--){
   var hit=wordHits[i];
   double angle=hit.Angle*Math.PI/180, cos=Math.Cos(angle), sin=Math.Sin(angle);
   double x=point.X-hit.X,y=point.Y-hit.Y;
   if(Math.Abs(x*cos+y*sin)<=hit.Width/2 && Math.Abs(-x*sin+y*cos)<=hit.Height/2)
    return hit.Start;
  }
  return -1;
 }
 int syllableWord=-1,syllableEnd;
 internal void ResetSyllableFocus(){syllableWord=-1;}
 internal void SeekWord(int start,int offset=0,int length=0){
  if(source==null||current==null)return;
  syllableWord=length>0?start:-1;
  syllableEnd=offset+length;
  int next=words.BinarySearch(new Word{Start=start},Comparer<Word>.Create((a,b)=>a.Start.CompareTo(b.Start)));
  int buffer=ContextBudget/3;
  if(next<0||start-pageStart<buffer&&pageStart>0||pageEnd-start<buffer&&pageEnd<source.TextLength){
   BuildPage(start,start+offset,true);
   return;
  }
  currentIndex=next;current=words[next];focusPartIndex=0;
  while(focusPartIndex+1<current.Parts.Count&&current.Parts[focusPartIndex+1].Source<=offset)focusPartIndex++;
  rasterDirty=true;flowMoving=true;LayoutFlow();Wake();Invalidate();
 }
 public KineticReader(){
  ResetFontFamily();
  
  SetStyle(ControlStyles.UserPaint|ControlStyles.Opaque|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);TabStop=true;Visible=false;BackColor=Paper;ForeColor=Ink;
  timer.Tick += (s,e) =>
  {
   double now = clock.Elapsed.TotalSeconds;
   double gap = previousTick > 0 ? now - previousTick : 0;
   double playbackNow = playbackClock.Elapsed.TotalSeconds;
   PollPlaybackModifier(playbackNow);
   bool stepped = narrationEnabled && playbackDirection>=0 ? AdvanceNarration() : AdvancePlayback(playbackNow);
   bool moving = Advance(Math.Max(0, Math.Min(.035, now - previousTick)));
   if(narrationEnabled&&narration!=null)narration.ObserveVisualTiming(gap*1000,(clock.Elapsed.TotalSeconds-now)*1000);
   previousTick = now;
   if (stepped || rasterDirty && moving) Invalidate();
   else if (moving)
   {
    int radius = (int)Math.Ceiling(OuterRingRadius) + 3;
    Invalidate(new Rectangle(Width / 2 - radius, Height / 2 - radius, radius * 2 + 1, radius * 2 + 1));
   }
   if (!moving && playbackDirection == 0) { timer.Stop(); clock.Stop(); }
  };
 }
 public void SetNarrationEngine(ReaderNarration engine){
  narration=engine;
  narration.Changed+=NotifyNarration;
  narration.WordStateChanged+=RefreshNarrationInk;
 }
 void NotifyNarration(){if(NarrationChanged!=null)NarrationChanged();}
 internal bool IsNarrationWordReady(int start){return narrationEnabled&&narration!=null&&narration.IsWordReady(start);}
 internal bool IsNarrationWordLoading(int start){return narrationEnabled&&narration!=null&&narration.IsWordLoading(start);}
 void RefreshNarrationInk(){
  rasterDirty=true;Invalidate();
  if(!resumeOnClip||!playWhenPrepared||playbackDirection!=0||!active||!Visible||current==null||!narration.HasWord(current.Start))return;
  resumeOnClip=playWhenPrepared=false;
  StartPlayback(1);
 }
 public void SetNarration(bool enabled){
  if(narrationEnabled==enabled)return;
  PausePlayback();
  narrationEnabled=enabled;
  prepareAfterText=false;
  narrationEnd=-1;
  lastPrefetchEnd=-1;
  if(!enabled&&narration!=null)narration.Reset();
  else RefreshNarrationInk();
  NotifyNarration();
 }
 public void TogglePlayback(){
  if(playbackDirection!=0||playWhenPrepared)PausePlayback();
  else StartPlayback(1);
 }
 public void ToggleNarrationPreparation(){
  if(!narrationEnabled||narration==null)return;
  if(narration.Preparing){resumeOnClip=playWhenPrepared=false;narration.CancelPreparation();return;}
  if(prepareAfterText){prepareAfterText=false;narration.Report("Preparation canceled.");return;}
  PrepareNextNarration();
 }
 public void PrepareNextNarration(){
  if(!narrationEnabled||narration==null||narration.Preparing||prepareAfterText||current==null)return;
  bool append=narration.HasWord(current.Start);
  if(!append)PausePlayback();
  if((append||narrationEnd>=0)&&NextStart(append?narration.PreparedEnd:narrationEnd)<0&&
     RequestMoreNarrationText!=null&&RequestMoreNarrationText()){
   prepareAfterText=true;
   narration.Report("Loading text for the next passage…");
   return;
  }
  PrepareNarration(false,append);
 }
 void PrepareNarration(bool play,bool append=false){
  if(source==null||current==null||narration==null)return;
  if(narration.Preparing){if(play)playWhenPrepared=true;return;}
  int start=current.Start;
  if(append||narrationEnd>=0){
   start=NextStart(append?narration.PreparedEnd:narrationEnd);
   if(start<0){narration.Report("End of the available text.");return;}
   if(!append)Seek(start);
  }
  int count=Math.Min(6000,source.TextLength-start);
  var passage=ReaderNarration.NextPassage(ReadRange(source,start,count),start,start+count<source.TextLength);
  if(passage==null){narration.Report("No readable passage here.");return;}
  if(!append)lastPrefetchEnd=-1;
  playWhenPrepared=play;
  narration.Prepare(new List<ReaderNarration.Passage>{passage},NarrationSource,append,success=>{
   bool resume=success&&playWhenPrepared;resumeOnClip=playWhenPrepared=false;
   if(resume&&playbackDirection==0&&narrationEnabled&&active&&Visible&&current!=null)StartPlayback(1);
  });
 }
 void PrefetchNarration(){
  if(narration==null||!narration.NeedsLookahead||narration.Preparing||lastPrefetchEnd==narration.PreparedEnd)return;
  lastPrefetchEnd=narration.PreparedEnd;
  if(NextStart(lastPrefetchEnd)>=0)PrepareNarration(false,true);
  else if(RequestMoreNarrationText!=null)RequestMoreNarrationText();
 }
 bool AdvanceNarration(){
  if(playbackDirection==0||!active||narration==null)return false;
  ReaderNarration.Cue cue;int end;
  if(narration.Read(out cue,out end)){
   bool changed=ApplyNarrationCue(cue);
   PrefetchNarration();
   return changed;
  }
  PausePlayback();
  if(end>=0&&source!=null){
   int next=NextStart(end);
   if(next>=0){
    Seek(next);
    if(narration.Preparing){resumeOnClip=playWhenPrepared=true;narration.Report("Preparing the next passage…");}
    else StartPlayback(1);
   }else{
    narrationEnd=end;
    if(RequestMoreNarrationText!=null&&RequestMoreNarrationText()){
     playWhenPrepared=true;
     narration.Report("Loading the next section…");
    }else narration.Report("End of the text.");
   }
  }
  return false;
 }
 internal bool ApplyNarrationCue(ReaderNarration.Cue cue){
  if(current==null)return false;
  if(current.Start!=cue.Word){
   int index=words.BinarySearch(new Word{Start=cue.Word},WordStartComparer);
   if(index<0)BuildPage(cue.Word,cue.Word,true);
   else{currentIndex=index;current=words[index];}
   if(current==null)return false;
  }
  double letter=cue.Phase*(current.Parts.Count-1);
  int indexInWord=(int)letter;
  double fraction=letter-indexInWord;
  bool changedWord=lastNarratedWord!=cue.Word;
  bool changed=indexInWord!=focusPartIndex||fraction!=narrationPhase||changedWord;
  focusPartIndex=indexInWord;narrationPhase=fraction;lastNarratedWord=cue.Word;
  if(changed){rasterDirty=true;flowMoving=true;if(changedWord&&Navigated!=null)Navigated(TextAnchor);}
  return changed;
 }
 static readonly IComparer<Word> WordStartComparer=Comparer<Word>.Create((a,b)=>a.Start.CompareTo(b.Start));
 int lastNarratedWord=-1;
 public void SetSpeed(double lps){lettersPerSecond=FlowTuning.ReadingSpeed.ClampInput(lps);if(playbackDirection!=0)nextWordTime=playbackClock.Elapsed.TotalSeconds+1.0/EffectiveLettersPerSecond;if(PlaybackChanged!=null)PlaybackChanged();}
 public void StartPlayback(int direction){
  ResetSyllableFocus();
  if(source==null||current==null||!active)return;
  if(narrationEnabled&&direction>=0){
   if(narration==null)return;
   if(narrationEnd>=0){
    int next=NextStart(narrationEnd);
    if(next<0){
     playWhenPrepared=RequestMoreNarrationText!=null&&RequestMoreNarrationText();
     narration.Report(playWhenPrepared?"Loading the next section…":"End of the text.");
     return;
    }
    Seek(next);
   }
   if(!narration.HasWord(current.Start)){PrepareNarration(true);return;}
   if(!narration.Play(current.Start,TextAnchor))return;
   playbackDirection=1;resumeOnClip=playWhenPrepared=false;flowMoving=true;Wake();PrefetchNarration();
  }else{
   if(ReadSlowPlaybackModifier!=null)SetPlaybackSlow(ReadSlowPlaybackModifier());
   playbackDirection=direction<0?-1:1;flowMoving=true;playbackClock.Restart();playbackSeconds=0;nextWordTime=1.0/EffectiveLettersPerSecond;Wake();
  }
  if(PlaybackChanged!=null)PlaybackChanged();
 }
 public void PausePlayback(){
  int previousDirection=playbackDirection;bool changed=previousDirection!=0;playbackDirection=0;resumeOnClip=playWhenPrepared=false;playbackClock.Stop();
  if(previousDirection>0&&narrationEnabled&&narration!=null&&current!=null)narration.Pause(current.Start,TextAnchor);
  if(changed&&PlaybackChanged!=null)PlaybackChanged();
 }
 public bool AdvancePlayback(double seconds) {
  playbackSeconds = seconds;
  if (playbackDirection == 0 || (narrationEnabled && playbackDirection>0) || !active || seconds < nextWordTime) return false;
  double interval = 1.0 / EffectiveLettersPerSecond;
  int advanced = 0;
  while (seconds >= nextWordTime && advanced < 16) {
   nextWordTime += interval;
   if (!Step(playbackDirection)) { PausePlayback(); break; }
   advanced++;
  }
  // Bound catch-up after a long stall while retaining the fractional letter phase.
  if (playbackDirection != 0 && seconds >= nextWordTime)
   nextWordTime += (Math.Floor((seconds - nextWordTime) / interval) + 1) * interval;
  return advanced > 0;
 }
 static string ReadRange(RuledReader reader,int start,int count){
  if(count<=0)return "";var memory=Marshal.AllocHGlobal((count+1)*2);
  try{Marshal.WriteInt16(memory,0);var range=new TextRange{Range=new Range{Start=start,End=start+count},Text=memory};int length=SendMessage(reader.Handle,0x44B,IntPtr.Zero,ref range).ToInt32();return length<=0?"":Marshal.PtrToStringUni(memory,length);}finally{Marshal.FreeHGlobal(memory);}
 }
 int NextStart(int position){
  while(position<source.TextLength){string text=ReadRange(source,position,Math.Min(1024,source.TextLength-position));var match=Regex.Match(text,@"\S+");if(match.Success)return position+match.Index;if(text.Length==0)break;position+=text.Length;}return -1;
 }
 int PreviousStart(int position){
  while(position>0){int start=Math.Max(0,position-1024);string text=ReadRange(source,start,position-start);var matches=Regex.Matches(text,@"\S+");
   if(matches.Count>0){int found=start+matches[matches.Count-1].Index;if(found==start&&start>0){position=start;continue;}return found;}position=start;
  }return -1;
 }
 int WordAt(int index){
  index=Math.Max(0,Math.Min(index,source.TextLength));if(index==source.TextLength){int previous=PreviousStart(index);return previous<0?0:previous;}
  // Session bookmarks point at exact word starts. Also normalize old line/character bookmarks.
  int begin=Math.Max(0,index-512);string text=ReadRange(source,begin,Math.Min(2048,source.TextLength-begin));
  foreach(Match match in Regex.Matches(text,@"\S+"))if(begin+match.Index+match.Length>index){if(begin+match.Index==begin&&begin>0&&begin+match.Index<index){int previous=PreviousStart(index);return previous<0?index:previous;}return begin+match.Index;}
  int next=NextStart(index);return next<0?Math.Max(0,PreviousStart(index)):next;
 }
 void Wake(){if(!Visible||source==null||!active||current==null)return;if(!timer.Enabled){clock.Restart();previousTick=0;timer.Start();}}
 public void ShowReader(RuledReader reader,int start,bool takeFocus=true){
  bool same=source==reader&&Visible&&current!=null&&TextAnchor==start&&readingFont!=null&&readingFont.Name==FontFamilyName&&Math.Abs(readingFont.SizeInPoints-15*Zoom)<.01&&Math.Abs(focusFont.Size/readingFont.Size-Tuning.Get("peakPercent")/100f)<.01;
  source=reader;Visible=true;if(!same){int word=WordAt(start);BuildPage(word,start);}BringToFront();if(takeFocus)Focus();
 }
 public void HideView(){EndEffectSizing(false);PausePlayback();prepareAfterText=false;NarrationSource=null;if(narration!=null)narration.Reset();narrationEnd=lastPrefetchEnd=-1;SetPlaybackSlow(false);timer.Stop();clock.Stop();Visible=false;source=null;current=null;focusPopWord=null;words.Clear();ringSizeScale=1f;}
 public void SetActive(bool value){active=value;if(!active){EndEffectSizing(false);PausePlayback();SetPlaybackSlow(false);timer.Stop();clock.Stop();}else{SetPlaybackSlow((ModifierKeys&Keys.Shift)!=0);Wake();}}
 protected override void OnResize(EventArgs e){base.OnResize(e);if(source!=null&&Visible)BuildPage(pageStart,TextAnchor);}
 public void Seek(int index){if(source==null)return;foreach(var word in words){word.SmokeReleased=false;word.SmokeSeen=false;}narrationEnd=-1;int target=WordAt(index);BuildPage(target,index);if(Navigated!=null)Navigated(TextAnchor);}
 void BuildPage(int start,int focus,bool exactStart=false){
  timer.Stop();rasterDirty=true;int focusWord=WordAt(focus);
  var old=new Dictionary<int,Word>();foreach(var item in words)old[item.Start]=item;
  float zoom=Zoom;
  bool sameFont=readingFont!=null&&readingFont.Name==FontFamilyName&&Math.Abs(readingFont.SizeInPoints-15*zoom)<.01&&Math.Abs(focusFont.Size/readingFont.Size-Tuning.Get("peakPercent")/100f)<.01;
  words.Clear();current=null;knownLength=source.TextLength;if(Width<80||Height<70)return;
  if(!sameFont){
   using(var family=new FontFamily(FontFamilyName)){
    var normal=new Font(family,15*zoom,FontStyle.Regular,GraphicsUnit.Point);
    var emphasis=new Font(family,15*zoom*Tuning.Get("peakPercent")/100f,family.IsStyleAvailable(FontStyle.Bold)?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Point);
    letterWidths.Clear();if(readingFont!=null)readingFont.Dispose();if(focusFont!=null)focusFont.Dispose();
    readingFont=normal;focusFont=emphasis;
   }
  }
  using(var g=CreateGraphics())using(var format=(StringFormat)StringFormat.GenericTypographic.Clone()){
   format.FormatFlags|=StringFormatFlags.MeasureTrailingSpaces;glyphHeight=readingFont.GetHeight(g);focusHeight=focusFont.GetHeight(g);if(!sameFont){
    raster.SetFonts(readingFont,focusFont,glyphHeight,focusHeight,g.DpiX,g.DpiY);
    smokeRaster.SetFonts(readingFont,focusFont,glyphHeight,focusHeight,g.DpiX,g.DpiY);
   }
   // Several complete offscreen rows on BOTH sides, sized for viewport and zoom.
   int budget=ContextBudget;start=WordAt(Math.Max(0,focusWord-budget));
   string text=ReadRange(source,start,Math.Min(budget*2+1024,source.TextLength-start));ContextCharacters=text.Length;cachedReadEnd=start+text.Length;
   var tokens=Regex.Matches(text,@"\S+");
   for(int i=0;i<tokens.Count;i++){
    var match=tokens[i];string value=match.Value;int absolute=start+match.Index;
    if(i==tokens.Count-1&&cachedReadEnd<source.TextLength&&match.Index+match.Length==text.Length&&absolute!=focusWord)break;
    Word word;
    if(!sameFont||!old.TryGetValue(absolute,out word)||word.Text!=value){
     word=new Word{Text=value,Start=absolute,End=absolute+value.Length,Bounds=new RectangleF(0,0,0,glyphHeight)};
     int[] letters=StringInfo.ParseCombiningCharacters(value);float x=0,fx=0;
     for(int j=0;j<letters.Length;j++){
      int offset=letters[j],count=(j+1<letters.Length?letters[j+1]:value.Length)-offset;
      string element=value.Substring(offset,count);
      FontStyle style=ReadGlyphStyle==null?FontStyle.Regular:ReadGlyphStyle(absolute+offset);
      string key=(char)style+element;
      SizeF widths;
      if(!letterWidths.TryGetValue(key,out widths)){
       widths=new SizeF(g.MeasureString(element,raster.GlyphFont(false,style),PointF.Empty,format).Width,
        g.MeasureString(element,raster.GlyphFont(true,style),PointF.Empty,format).Width);
       if(letterWidths.Count<4096)letterWidths[key]=widths;
      }
      word.Parts.Add(new Part{Text=element,Style=style,Source=offset,X=x,Width=widths.Width,FocusX=fx,FocusWidth=widths.Height});
      x+=widths.Width;fx+=widths.Height;
     }
     word.Bounds=new RectangleF(0,0,x,glyphHeight);word.FocusWidth=fx;
    }
    words.Add(word);if(absolute==focusWord)current=word;
   }
  }
  if(current==null&&words.Count>0)current=words[words.Count-1];focusPartIndex=0;
  if(current!=null)for(int i=0;i<current.Parts.Count;i++)if(current.Start+current.Parts[i].Source<=focus)focusPartIndex=i;
  // Normal refill keeps the old visible glyphs and their spring state intact.
  if(exactStart&&sameFont&&old.ContainsKey(focusWord)&&old[focusWord]==current){
   var retained=new HashSet<int>();foreach(var w in words)retained.Add(w.Start);
   int first=pageStart,last=pageEnd;
   if(words.Count>0){first=words[0].Start;last=words[words.Count-1].End;}
   foreach(var w in old.Values)if(!retained.Contains(w.Start))foreach(var p in w.Parts){float y=ReadingPoint.Y+RibbonRow(p.Flow,p.Role)*RowStep;if(y>=-Tuning.Force-glyphHeight&&y<=Height+Tuning.Force+glyphHeight){first=Math.Min(first,w.Start);last=Math.Max(last,w.End);break;}}
   foreach(var w in old.Values)if(!retained.Contains(w.Start)&&w.Start>=first&&w.End<=last){words.Add(w);retained.Add(w.Start);}
   words.Sort((a,b)=>a.Start.CompareTo(b.Start));
   foreach(var w in old.Values)if(!retained.Contains(w.Start))foreach(var p in w.Parts){float y=ReadingPoint.Y+RibbonRow(p.Flow,p.Role)*RowStep;if(y>=-Tuning.Force-glyphHeight&&y<=Height+Tuning.Force+glyphHeight)VisibleCacheDrops++;}
  }
  pageStart=words.Count==0?start:words[0].Start;pageEnd=words.Count==0?start:words[words.Count-1].End;
  currentIndex=words.IndexOf(current);flowMoving=true;LayoutFlow();Wake();Invalidate();
 }
 float PartWidth(Word word,Part part){return part.Width+(part.FocusWidth-part.Width)*part.Emphasis;}
 float SingleLineX(Word word,Part part,float width)
 {
  var focus=current.Parts[focusPartIndex];
  float activeX=ReadingPoint.X+focus.Flow+PartWidth(current,focus)*.5f;
  float readingX=Math.Max(Width*.38f,Math.Min(Width*.62f,activeX));
  return ReadingPoint.X+part.Flow+width*.5f+readingX-activeX;
 }
 float PartX(Word word,Part part){return part.LayoutX;}
 float PartHeight(Part part){return glyphHeight+(focusHeight-glyphHeight)*part.Emphasis;}
 float WaveRadius{get{return Math.Min(RibbonWidth*.34f,Math.Max(100,glyphHeight*3.4f)*Tuning.Get("waveReach")/100f);}}
 public float MagnificationAt(float distance){return (float)Math.Exp(-distance*distance/(2*WaveRadius*WaveRadius));}
 float PartCenter(Word word,Part part){return word.Tape+PartX(word,part)+PartWidth(word,part)/2;}
 void LayoutFlow(){
  if(current==null)return;float gap=Math.Max(10,glyphHeight*.24f),tape=0;
  foreach(var word in words){word.Tape=tape;float x=0;foreach(var part in word.Parts){part.LayoutX=x;x+=PartWidth(word,part);}tape+=x+gap;}
  var chosen=current.Parts[focusPartIndex];float anchor=PartCenter(current,chosen);
  if(CenterWholeWord&&playbackDirection==0){
   var first=current.Parts[0];var last=current.Parts[current.Parts.Count-1];
   if(syllableWord==current.Start){
    first=current.Parts[focusPartIndex];last=first;
    for(int i=focusPartIndex+1;i<current.Parts.Count&&current.Parts[i].Source<syllableEnd;i++)last=current.Parts[i];
   }
   anchor=current.Tape+(first.LayoutX+last.LayoutX+PartWidth(current,last))/2;
  }
  // The clock moves between graphemes every frame, including across word spaces.
  if(playbackDirection>0&&narrationEnabled){
   int next=focusPartIndex+1;
   if(next<current.Parts.Count)
    anchor+=(PartCenter(current,current.Parts[next])-anchor)*(float)narrationPhase;
  }else if(playbackDirection!=0){
   Word neighbour=current;int index=focusPartIndex+playbackDirection;
   if(index<0||index>=current.Parts.Count){int wi=currentIndex+playbackDirection;if(wi>=0&&wi<words.Count){neighbour=words[wi];index=playbackDirection>0?0:neighbour.Parts.Count-1;}else index=focusPartIndex;}
   double interval=1.0/EffectiveLettersPerSecond;float phase=(float)Math.Max(0,Math.Min(1,(playbackSeconds-nextWordTime+interval)/interval));
   anchor+=(PartCenter(neighbour,neighbour.Parts[index])-anchor)*phase;
  }
  foreach(var word in words)foreach(var part in word.Parts){
   float target=word.Tape+PartX(word,part)-anchor;
   if(part.Role==2){part.Flow=target;part.Velocity=0;}
   part.Target=target;part.Role=word==current?0:word.Start<current.Start?-1:1;
  }
 }
 public void RefreshContent(){
  if(source==null)return;int oldLength=knownLength;knownLength=source.TextLength;
  if(cachedReadEnd>=oldLength&&knownLength!=oldLength)BuildPage(pageStart,TextAnchor);
  if(narrationEnabled){
   if(knownLength!=oldLength)lastPrefetchEnd=-1;
   if(prepareAfterText){prepareAfterText=false;PrepareNextNarration();}
   if(playWhenPrepared&&narrationEnd>=0&&!NarrationPreparing)StartPlayback(1);
   else if(playbackDirection!=0)PrefetchNarration();
  }
 }
 public void NarrationTextUnavailable(){
  if(prepareAfterText){prepareAfterText=false;narration.Report("Could not load the next section.");}
  if(playWhenPrepared&&narrationEnd>=0){playWhenPrepared=false;narration.Report("Could not load the next section.");}
 }
 public bool Step(int direction){
  if(source==null||current==null)return false;
  narrationEnd=-1;
  int wi=currentIndex,index=focusPartIndex+direction,target;Word next=current;
  if(index<0||index>=current.Parts.Count){wi+=direction;
   if(wi>=0&&wi<words.Count){next=words[wi];index=direction>0?0:next.Parts.Count-1;}
   else next=null;
  }
  if(next!=null)target=next.Start+next.Parts[index].Source;
  else if(direction>0)target=NextStart(current.End);
  else{int previous=PreviousStart(current.Start);if(previous<0)return false;string value=ReadRange(source,previous,current.Start-previous).TrimEnd();int[] letters=StringInfo.ParseCombiningCharacters(value);target=previous+(letters.Length==0?0:letters[letters.Length-1]);}
  if(target<0)return false;
  int buffer=ContextBudget/3;
  if(next==null||target-pageStart<buffer&&pageStart>0||pageEnd-target<buffer&&pageEnd<source.TextLength)BuildPage(target,target,true);
  else{current=next;currentIndex=wi;focusPartIndex=index;if(playbackDirection==0){LayoutFlow();Wake();Invalidate();}}
  rasterDirty=true;flowMoving=true;if(Navigated!=null)Navigated(TextAnchor);return true;
 }
 public bool HandleKey(Keys key){
  if(key==(Keys.Shift|Keys.Space))key=Keys.Space;
  if(source==null)return false;
  if(key==Keys.Right){PausePlayback();Step(1);return true;}
  if(key==Keys.Left){PausePlayback();Step(-1);return true;}
  if(key==Keys.Space){TogglePlayback();return true;}
  if(key==Keys.C&&narrationEnabled){PrepareNextNarration();return true;}
  if(key==Keys.Back){PausePlayback();Step(-1);return true;}
  if(key==Keys.Home||key==(Keys.Control|Keys.Home)){PausePlayback();Seek(0);return true;}
  if(key==Keys.End||key==(Keys.Control|Keys.End)){PausePlayback();Seek(source.TextLength);return true;}
  if(key==(Keys.Control|Keys.C)){if(current!=null)Clipboard.SetText(current.Text);return true;}
  return key==Keys.Z||key==Keys.PageDown||key==Keys.PageUp;
 }
 public void BeginEffectSizing(Point position)
 {
  if (IsSizingEffects || !active || !Visible || current == null) return;
  sizingCircleRadius = Tuning.Get("circleRadius");
  sizingLavaRadius = Tuning.Radius;
  sizingPreviousX = position.X;
  sizingLogScale = 0;
  IsSizingEffects = true;
  Capture = true;
  Cursor = Cursors.SizeWE;
 }
 public bool HandleEffectSizingKey(Keys key, bool repeat)
 {
  if (!IsSizingEffects) return false;
  if (!repeat)
  {
   if (key == Keys.Escape || key == Keys.Back) EndEffectSizing(false);
   else if (key == Keys.F || key == Keys.Enter) EndEffectSizing(true);
  }
  return true;
 }
 void MoveEffectSizing(int x, bool fine)
 {
  int delta = x - sizingPreviousX;
  sizingPreviousX = x;
  double minimum = Math.Max((double)FlowTuning.CircleRadius.InputMin / sizingCircleRadius, (double)FlowTuning.LavaRadius.InputMin / sizingLavaRadius);
  double maximum = Math.Min((double)FlowTuning.CircleRadius.InputMax / sizingCircleRadius, (double)FlowTuning.LavaRadius.InputMax / sizingLavaRadius);
  sizingLogScale = Math.Max(Math.Log(minimum), Math.Min(Math.Log(maximum),
   sizingLogScale + delta * .006 * (fine ? 1.0 / 3 : 1)));
  double scale = Math.Exp(sizingLogScale);
  SetTuning("circleRadius", (int)Math.Round(sizingCircleRadius * scale));
  SetTuning("radius", (int)Math.Round(sizingLavaRadius * scale));
 }
 public void EndEffectSizing(bool confirm)
 {
  if (!IsSizingEffects) return;
  IsSizingEffects = false;
  Capture = false;
  Cursor = Cursors.Default;
  if (!confirm)
  {
   SetTuning("circleRadius", sizingCircleRadius);
   SetTuning("radius", sizingLavaRadius);
  }
  else if (EffectSizeCommitted != null) EffectSizeCommitted();
 }
 protected override void OnMouseMove(MouseEventArgs e)
 {
  if (IsSizingEffects) MoveEffectSizing(e.X, (ModifierKeys & Keys.Shift) != 0);
  base.OnMouseMove(e);
 }
 protected override void OnMouseDown(MouseEventArgs e)
 {
  if (IsSizingEffects)
  {
   if (e.Button == MouseButtons.Left) EndEffectSizing(true);
   else if (e.Button == MouseButtons.Right) EndEffectSizing(false);
   return;
  }
  base.OnMouseDown(e);
 }
 protected override void OnMouseCaptureChanged(EventArgs e)
 {
  if (IsSizingEffects && !Capture) EndEffectSizing(false);
  base.OnMouseCaptureChanged(e);
 }
 protected override void OnLostFocus(EventArgs e)
 {
  EndEffectSizing(false);
  base.OnLostFocus(e);
 }
 public void ApplyWheel(int delta){if(!IsSizingEffects&&source!=null&&delta!=0&&ZoomRequested!=null)ZoomRequested(source.ZoomFactor+delta/120f*.1f);}
 protected override void OnMouseWheel(MouseEventArgs e){if(WheelOverride==null)ApplyWheel(e.Delta);else WheelOverride(e.Delta,(ModifierKeys&Keys.Control)!=0);var handled=e as HandledMouseEventArgs;if(handled!=null)handled.Handled=true;base.OnMouseWheel(e);}
 PaintState CreatePaintState(){
  float radius=Tuning.Radius;
  return new PaintState{
   CenterX=ReadingPoint.X,CenterY=ReadingPoint.Y,Width=RibbonWidth,RowStep=RowStep,Radius=radius,
   RadiusX=Math.Min(Width*.45f,Math.Max(radius,(fieldWidth/2+100)*Tuning.Get("wordReach")/100f)),
   RadiusY=radius*Tuning.Get("verticalReach")/100f,Force=Tuning.Force,
   Rotation=Tuning.Rotation,RotationResponse=Tuning.Get("rotationResponse"),HeatTint=Tuning.HeatTint
  };
 }
 static PointF Spread(float x,float y,ref PaintState paint){
  float dx=x-paint.CenterX,dy=y-paint.CenterY,rx=paint.RadiusX,ry=paint.RadiusY;
  double distanceSquared=dx*dx/(rx*rx)+dy*dy/(ry*ry);
  if(distanceSquared>=1)return PointF.Empty;
  double distance=Math.Sqrt(distanceSquared),length=Math.Sqrt(dx*dx+dy*dy);
  if(length<.1)return PointF.Empty;float amount=(float)(paint.Force*Math.Pow(1-distance,1.4));return new PointF((float)(dx/length*amount),(float)(dy/length*amount));
 }
 bool AdvanceRingSize(float dt)
 {
  // The last grapheme's layout includes all of the preceding, already magnified widths.
  var last = current.Parts[current.Parts.Count - 1];
  float wordWidth = last.LayoutX + PartWidth(current, last);
  float referenceWidth = 3f * glyphHeight;
  float target = 1f + .5f * (wordWidth - referenceWidth) / (wordWidth + referenceWidth);
  float previous = ringSizeScale;
  if (Tuning.Get("circleVisible") == 0 || Math.Abs(target - previous) < .0002f)
   ringSizeScale = target;
  else
   ringSizeScale += (target - previous) * (1f - (float)Math.Exp(-9f * dt));
  return Tuning.Get("circleVisible") != 0 && ringSizeScale != previous;
 }
 public bool Advance(double seconds){
  if(!active||current==null||readingFont==null)return false;float dt=(float)Math.Max(0,Math.Min(.035,seconds));bool moving=false;
  bool popping=AdvanceFocusPop(seconds);
  bool ring=Tuning.Get("circleVisible")!=0&&Tuning.Get("circleSpeed")!=0;if(ring)circleAngle=(circleAngle-dt*Tuning.Get("circleSpeed"))%360;
  if(!flowMoving&&((narrationEnabled&&playbackDirection>0)||playbackDirection==0))return ring||popping;
  // A broad spatial wave grows each letter on approach, peaks at center and falls away.
  // It is independent of word selection, with monotonic easing rather than rebouncing.
  float blend=1-(float)Math.Exp(-dt*Tuning.Get("growthEase"));
  float wave=WaveRadius,inverseWave=1/(2*wave*wave),cutoff=wave*5;
  foreach(var word in words)foreach(var part in word.Parts){
   float distance=part.Flow+PartWidth(word,part)/2,target=Math.Abs(distance)>cutoff?0:(float)Math.Exp(-distance*distance*inverseWave);
   float change=(target-part.Emphasis)*blend;part.Emphasis+=change;
   if(Math.Abs(target-part.Emphasis)<.0002f)part.Emphasis=target;else moving=true;
  }
  foreach(var word in words){float nearest=float.MaxValue;foreach(var part in word.Parts)nearest=Math.Min(nearest,Math.Abs(part.Flow+PartWidth(word,part)/2));
   float t=Math.Max(0,Math.Min(1,(nearest-wave)/Math.Max(1,wave*.75f))),target=word==current?0:t*t*(3-2*t);
   word.Lava+=(target-word.Lava)*blend;if(Math.Abs(target-word.Lava)<.0002f)word.Lava=target;else moving=true;
  }
  float wantedWidth=current.FocusWidth;fieldWidth+=(wantedWidth-fieldWidth)*blend;
  if(Math.Abs(wantedWidth-fieldWidth)<.05f)fieldWidth=wantedWidth;else moving=true;
  LayoutFlow();
  bool ringResized = AdvanceRingSize(dt);
  // One translation spring per word keeps all of its letters joined; local sizes
  // change smoothly and accumulate into spacing, rather than independent letter drifts.
  foreach(var word in words){if(word.Parts.Count==0)continue;var first=word.Parts[0];
   for(float remaining=dt;remaining>0;){float step=Math.Min(.008f,remaining);remaining-=step;first.Velocity+=((first.Target-first.Flow)*Tuning.Spring-first.Velocity*Tuning.Damping)*step;first.Flow+=first.Velocity*step;}
   if(Math.Abs(first.Target-first.Flow)<Math.Max(.02f,Math.Abs(first.Target)*.000001f)&&Math.Abs(first.Velocity)<Math.Max(.2f,Math.Abs(first.Target)*.00003f)){first.Flow=first.Target;first.Velocity=0;}else moving=true;
   foreach(var part in word.Parts){part.Flow=first.Flow+part.LayoutX;part.Velocity=first.Velocity;}
  }
  if(DissolvingLine)ConstrainReadingLine();
  rasterDirty=true;flowMoving=moving||ringResized;return flowMoving||ring||popping;
 }
 // Keep the active word anchored while its neighbours make room for changing widths.
 void ConstrainReadingLine()
 {
  if(currentIndex<0)return;
  float gap=Math.Max(10,glyphHeight*.24f);
  for(int direction=-1;direction<=1;direction+=2)
  {
   for(int index=currentIndex+direction;index>=0&&index<words.Count;index+=direction)
   {
    var word=words[index];
    var neighbour=words[index-direction];
    if(word.Parts.Count==0||neighbour.Parts.Count==0)continue;
    var first=word.Parts[0];
    var last=word.Parts[word.Parts.Count-1];
    var adjacentLast=neighbour.Parts[neighbour.Parts.Count-1];
    float shift=direction>0
     ? Math.Max(0,adjacentLast.Flow+PartWidth(neighbour,adjacentLast)+gap-first.Flow)
     : Math.Min(0,neighbour.Parts[0].Flow-gap-last.Flow-PartWidth(word,last));
    if(shift==0)continue;
    foreach(var part in word.Parts)
    {
     part.Flow+=shift;
     part.Velocity=neighbour.Parts[0].Velocity;
    }
   }
  }
 }

 bool transparentRaster;
 bool ComposeRaster(bool transparent, Color readInk=default(Color), Color unreadInk=default(Color)){
  if(readingFont==null||current==null||Width<=0||Height<=0){wordHits.Clear();return false;}
  if(!rasterDirty&&transparentRaster==transparent&&rasterReadInk==readInk&&rasterUnreadInk==unreadInk&&rasterSmokeFade==SmokeFade)return false;
  long tick=Stopwatch.GetTimestamp();
  PaintedWords=PaintedLetters=0;wordHits.Clear();raster.Begin(Width,Height,transparent);
  bool dissolve=transparent&&DissolvingLine;
  smokeWeight=smokeArea=smokeX=smokeY=smokeVelocity=0;
  smokeWords=0;smokeRasterOpen=false;
   var paint=CreatePaintState();
   paint.ReadInk=readInk;
   paint.UnreadInk=unreadInk;
   paint.FocusRow=RibbonRow(current.Parts[focusPartIndex].Flow,0);
   try{
    foreach(var word in words)if(word!=current)DrawWord(word,ref paint);
    if(ReadingBloom)DrawWord(current,ref paint,true);
    DrawWord(current,ref paint);
   }finally{raster.End();if(smokeRasterOpen)smokeRaster.End();}
  if(dissolve&&smokeWeight>0&&SmokeReleased!=null)
   SmokeReleased(new SmokeHandoff { Mask=smokeRaster.Frame,X=smokeX/smokeWeight/Width,
    Y=smokeY/smokeWeight/Height,Velocity=smokeVelocity/smokeWeight/Width,Release=smokeWeight/smokeArea,Words=smokeWords });
  rasterDirty=false;transparentRaster=transparent;rasterReadInk=readInk;rasterUnreadInk=unreadInk;rasterSmokeFade=SmokeFade;
  CompositePaintMs=(Stopwatch.GetTimestamp()-tick)*1000.0/Stopwatch.Frequency;
  return true;
 }
 internal Bitmap RenderOverlay(Color readInk, Color unreadInk) {
  return ComposeRaster(true,readInk,unreadInk) ? raster.Frame : null;
 }
 internal Rectangle OverlayUploadBounds {
  get {
   if(!DissolvingLine||!SeparateSmoke)return new Rectangle(0,0,Width,Height);
   // Bounds come from the actual transformed glyph masks, including bloom.
   var band=raster.PaintedBand;
   if(band.Height==0)return new Rectangle(0,(int)ReadingPoint.Y,Width,1);
   int top=Math.Max(0,band.Top-2),bottom=Math.Min(Height,band.Bottom+2);
   return new Rectangle(0,top,Width,Math.Max(1,bottom-top));
  }
 }
 public void Render(Graphics g){
  if(readingFont==null||current==null||Width<=0||Height<=0){wordHits.Clear();g.Clear(Paper);return;}
  ComposeRaster(false);
  long tick=Stopwatch.GetTimestamp();raster.Paint(g);DrawCircle(g);BlitPaintMs=(Stopwatch.GetTimestamp()-tick)*1000.0/Stopwatch.Frequency;
 }
 void DrawCircle(Graphics graphics)
 {
  if (Tuning.Get("circleVisible") == 0) return;
  int outerRadius = Tuning.Get("circleRadius");
  if (outerRadius != cachedCircleRadius)
  {
   for (int ring = 0; ring < ringPaths.Length; ring++)
   {
    if (ringPaths[ring] != null) ringPaths[ring].Dispose();
    float scale = RingScales[ring];
    float radius = outerRadius * scale;
    int dotCount = (int)Math.Round(40 * scale);
    var path = new System.Drawing.Drawing2D.GraphicsPath();
    for (int dot = 0; dot < dotCount; dot++)
    {
     path.StartFigure();
     path.AddArc(-radius, -radius, 2 * radius, 2 * radius, dot * 360f / dotCount, 1.2f / scale);
    }
    ringPaths[ring] = path;
   }
   cachedCircleRadius = outerRadius;
  }

  var state = graphics.Save();
  try
  {
   graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
   graphics.TranslateTransform(ReadingPoint.X, ReadingPoint.Y);
   graphics.ScaleTransform(ringSizeScale, ringSizeScale);
   using (var pen = new Pen(Color.FromArgb(Tuning.Get("circleOpacity"), 144, 177, 196), .9f / ringSizeScale))
   {
    pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
    graphics.RotateTransform((float)circleAngle);
    graphics.DrawPath(pen, ringPaths[0]);
    graphics.RotateTransform((float)(-2 * circleAngle));
    graphics.DrawPath(pen, ringPaths[1]);
    graphics.RotateTransform((float)(2 * circleAngle));
    graphics.DrawPath(pen, ringPaths[2]);
   }
  }
  finally
  {
   graphics.Restore(state);
  }
 }
 void ReleaseWord(Word word)
 {
  if(word.SmokeReleased||!word.SmokeSeen||word.Parts.Count==0)return;
  word.SmokeReleased=true;
  word.SmokeScale=.6f+1.2f*(float)smokeSizes.NextDouble();
  var first=word.Parts[0];var last=word.Parts[word.Parts.Count-1];
  float left=first.LastX-first.LastWidth*.5f;
  float right=last.LastX+last.LastWidth*.5f;
  float width=Math.Max(1,right-left),height=1;
  foreach(var part in word.Parts)height=Math.Max(height,part.LastHeight);
  // A word stays a single shape. Fit exceptionally long words into the field.
  float scale=Math.Min(word.SmokeScale,Math.Min(Width*.90f/width,Height*.80f/height));
  float cx=Math.Max(width*scale*.5f,Math.Min(Width-width*scale*.5f,(left+right)*.5f));
  float cy=ReadingPoint.Y;
  if(!smokeRasterOpen){smokeRaster.Begin(Width,Height,true);smokeRasterOpen=true;}
  foreach(var part in word.Parts)
  {
   float x=cx+(part.LastX-(left+right)*.5f)*scale;
   float e=part.LastEmphasis;
   if(e<1)smokeRaster.Glyph(part.Text,false,part.Width,x,cy,part.LastWidth*scale/Math.Max(.1f,part.Width),
    part.LastHeight*scale/glyphHeight,0,Color.White,(int)(255*(1-e)),part.Style,false);
   if(e>0)smokeRaster.Glyph(part.Text,true,part.FocusWidth,x,cy,part.LastWidth*scale/Math.Max(.1f,part.FocusWidth),
    part.LastHeight*scale/focusHeight,0,Color.White,(int)(255*e),part.Style,false);
  }
  float weight=width*height;
  smokeArea+=weight;smokeWeight+=weight;smokeX+=cx*weight;smokeY+=cy*weight;
  smokeVelocity+=first.Velocity*weight;smokeWords++;
 }

 void DrawWord(Word word,ref PaintState paint,bool bloom=false){
  if(word.Parts.Count==0)return;
  if(DissolvingLine&&!paint.ReadInk.IsEmpty&&!SeparateSmoke&&(word.Start<current.Start||SmokeFade>0)){
   if(!bloom)ReleaseWord(word);return;
  }
  float cx=paint.CenterX,cy=paint.CenterY,w=paint.Width,rowStep=paint.RowStep;
  var first=word.Parts[0];var last=word.Parts[word.Parts.Count-1];
  if(DissolvingLine&&!paint.ReadInk.IsEmpty){
   float left=SingleLineX(word,first,PartWidth(word,first))-PartWidth(word,first)*.5f;
   float right=SingleLineX(word,last,PartWidth(word,last))+PartWidth(word,last)*.5f;
   if(right<0||left>Width)return;
  }
  float top=cy+(int)Math.Floor((cx+first.Flow)/w)*rowStep,bottom=cy+(int)Math.Floor((cx+(last.Flow+PartWidth(word,last)+2))/w)*rowStep;
  bool pop=!DissolvingLine&&word==current&&word==focusPopWord;
  float popCenter=cx+(first.Flow+last.Flow+PartWidth(word,last))/2;
  float ry=paint.RadiusY,pad=focusHeight/2+4;
  if(!DissolvingLine&&(bottom+pad<0&&bottom<cy-ry||top-pad>Height&&top>cy+ry))return;
  bool read=word.Start<current.Start,ready=IsNarrationWordReady(word.Start),loading=!ready&&IsNarrationWordLoading(word.Start);
  bool useContextInk=!paint.ReadInk.IsEmpty;
  bool heat=!useContextInk&&!ready&&!loading&&paint.HeatTint&&word!=current;
  Color natural=ready?(read?ReadReadyInk:ReadyInk):loading?LoadingInk:(read?Color.FromArgb(165,172,180):Ink);
  Color focus=ready?ReadyFocusInk:loading?LoadingFocusInk:FocusInk;float lava=word==current?0:word.Lava;
  if(!bloom)PaintedWords++;
  int firstPart=bloom?focusPartIndex:0,endPart=bloom?focusPartIndex+1:word.Parts.Count;
  for(int partIndex=firstPart;partIndex<endPart;partIndex++){
   var part=word.Parts[partIndex];
   if(DissolvingLine&&!paint.ReadInk.IsEmpty&&!bloom){
    part.LastWidth=PartWidth(word,part);part.LastHeight=PartHeight(part);part.LastEmphasis=part.Emphasis;
    part.LastX=SingleLineX(word,part,part.LastWidth);
    if(part.LastX+part.LastWidth>0&&part.LastX-part.LastWidth<Width)word.SmokeSeen=true;
   }
   float raw=cx+part.Flow;int row=(int)Math.Floor(raw/w);float y=cy+row*rowStep;
   if(!DissolvingLine&&(y+pad<0&&y<cy-ry||y-pad>Height&&y>cy+ry))continue;
   float e=Math.Max(0,Math.Min(1,part.Emphasis));if(e<.0002f)e=0;
   bool focused=word==current&&partIndex==focusPartIndex;
   Color ink;
   if(useContextInk){
    bool alreadyRead=read||(word==current&&partIndex<focusPartIndex);
    ink=alreadyRead?paint.ReadInk:paint.UnreadInk;
   }else{
    Color baseInk=natural;if(heat){float amount=Math.Max(0,1-Math.Abs(part.Flow)/Math.Max(1,paint.Radius));baseInk=Color.FromArgb((int)(baseInk.R+(241-baseInk.R)*amount),(int)(baseInk.G+(168-baseInk.G)*amount),(int)(baseInk.B+(105-baseInk.B)*amount));}
    float brightness=ReadingBloom&&focused?.65f+.35f*e:e;
    ink=Color.FromArgb((int)(baseInk.R+(focus.R-baseInk.R)*brightness),(int)(baseInk.G+(focus.G-baseInk.G)*brightness),(int)(baseInk.B+(focus.B-baseInk.B)*brightness));
    if(ReadingBloom&&read)
     ink=Color.FromArgb(ink.R*52/100,ink.G*52/100,ink.B*52/100);
   }
   float width=PartWidth(word,part),height=PartHeight(part);
   if(pop){raw=popCenter+(raw-popCenter)*focusPop.Width;width*=focusPop.Width;height*=focusPop.Height;row=(int)Math.Floor(raw/w);y=cy+row*rowStep;}
   if(DissolvingLine&&!paint.ReadInk.IsEmpty){row=0;y=cy;}
   float x=DissolvingLine&&!paint.ReadInk.IsEmpty?SingleLineX(word,part,width)-width*.5f:raw-row*w;DrawRibbonPart(word.Start,part,ink,x+width/2,y,width,height,e,lava,row,ref paint,bloom);
   if(!bloom)PaintedLetters++;
   if(!DissolvingLine&&x+width+2>w)DrawRibbonPart(word.Start,part,ink,x-w+width/2,y+rowStep,width,height,e,lava,row+1,ref paint,bloom);
  }
 }
 static Color ContextInk(Color edgeInk,float x,ref PaintState paint){
  double distance=Math.Abs((x-paint.CenterX)/paint.CenterX);
  const double whiteWidth=.25;
  if(distance<=whiteWidth)return Color.White;
  if(distance>=1)return edgeInk;
  double t=(distance-whiteWidth)/(1-whiteWidth);
  double white=1-t*t*(3-2*t);
  return Color.FromArgb(
   (int)Math.Round(edgeInk.R+(255-edgeInk.R)*white),
   (int)Math.Round(edgeInk.G+(255-edgeInk.G)*white),
   (int)Math.Round(edgeInk.B+(255-edgeInk.B)*white));
 }
 void DrawRibbonPart(int start,Part part,Color ink,float x,float y,float width,float height,float e,float lava,int row,ref PaintState paint,bool bloom){
  if(DissolvingLine&&(x+width<0||x-width>Width))return;
  var push=DissolvingLine||lava==0?PointF.Empty:Spread(x,y,ref paint);float dx=push.X*lava,dy=push.Y*lava;
  if(y+dy+height<0||y+dy-height>Height)return;
  int offset=start+part.Source;
  if(selectionStart<selectionEnd && offset<selectionEnd && offset+part.Text.Length>selectionStart)
   ink=SelectionInk;
  else if(!paint.ReadInk.IsEmpty&&row==paint.FocusRow)
   ink=ContextInk(ink,x+dx,ref paint);
  float angle=Math.Max(-paint.Rotation,Math.Min(paint.Rotation,dx*paint.RotationResponse/100f));
  if(TrackWordHits&&!bloom)wordHits.Add(new WordHit{Start=start,X=x+dx,Y=y+dy,Width=width,Height=height,Angle=angle});
  int opacity=bloom?176:255;
  if(DissolvingLine&&!paint.ReadInk.IsEmpty){
   ink=singleLineInk;
  }
  if(e<1)raster.Glyph(part.Text,false,part.Width,x+dx,y+dy,width/Math.Max(.1f,part.Width),height/glyphHeight,angle,ink,(int)(opacity*(1-e)),part.Style,bloom);
  if(e>0)raster.Glyph(part.Text,true,part.FocusWidth,x+dx,y+dy,width/Math.Max(.1f,part.FocusWidth),height/focusHeight,angle,ink,(int)(opacity*e),part.Style,bloom);
 }
 protected override void OnPaint(PaintEventArgs e){
  base.OnPaint(e);Render(e.Graphics);paintCount++;
  if(active&&current!=null&&narrationEnabled&&narration!=null)narration.ObserveVisualTiming(0,CompositePaintMs+BlitPaintMs);
 }
 protected override void Dispose(bool disposing){if(disposing){PausePlayback();if(narration!=null){narration.Changed-=NotifyNarration;narration.WordStateChanged-=RefreshNarrationInk;narration.Dispose();}timer.Dispose();letterWidths.Clear();raster.Dispose();smokeRaster.Dispose();for(int ring=0;ring<ringPaths.Length;ring++){if(ringPaths[ring]!=null){ringPaths[ring].Dispose();ringPaths[ring]=null;}}cachedCircleRadius=0;clock.Stop();playbackClock.Stop();if(readingFont!=null)readingFont.Dispose();if(focusFont!=null)focusFont.Dispose();}base.Dispose(disposing);}
}





