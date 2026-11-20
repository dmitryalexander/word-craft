using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

sealed class ReaderNarration : IDisposable {
 const string ModelRevision = "92741c5b07d085ff840e26fea8ce43110b87852f";
 const int LookaheadMilliseconds = 75000;
 static readonly SemaphoreSlim WorkerGate = new SemaphoreSlim(1, 1);
 static readonly Regex WordPattern = new Regex(@"\S+");
 static readonly Regex SentenceEnd = new Regex("[.!?…][\"'”’»)}\\]]*$");
 static readonly Regex Abbreviation = new Regex(@"^(?:[A-Za-z]|Mr|Mrs|Ms|Dr|St|Prof|Sr|Jr|etc)\.$", RegexOptions.IgnoreCase);
 static readonly Regex LineBreak = new Regex(@"\r\n|\r|\n");
 static readonly Regex Initials = new Regex(@"^(?<prefix>[""'“‘(\[]*)(?<letters>(?:[A-Z]\.)+)(?<suffix>[,""'”’»)}\]]*)$");
 static readonly Regex RomanNumber = new Regex(@"^(?<number>X{0,3}(?:IX|IV|V?I{0,3}))(?<suffix>[:.]?)$");
 static readonly string[] LetterNames = "ay bee see dee ee eff gee aitch eye jay kay el em en oh pee cue ar ess tee you vee double-you ex why zee".Split(' ');
 internal static bool IsSentenceEnd(string word) {
  return SentenceEnd.IsMatch(word) && !Abbreviation.IsMatch(word.Trim('"','\'','“','”','‘','’','(',')','[',']')) &&
   !Initials.IsMatch(word);
 }

 internal sealed class Passage {
  public string Text;
  public string[] Words;
  public int[] Starts;
  public int End;
 }

 static Match ChapterNumber(string heading, string number) {
  if (!heading.Equals("chapter", StringComparison.OrdinalIgnoreCase)) return null;
  Match match = RomanNumber.Match(number);
  return match.Success && match.Groups["number"].Length > 0 ? match : null;
 }

 static string SpokenChapterNumber(Match number) {
  int value = 0, previous = 0;
  string roman = number.Groups["number"].Value;
  for (int i = roman.Length - 1; i >= 0; i--) {
   int part = roman[i] == 'X' ? 10 : roman[i] == 'V' ? 5 : 1;
   value += part < previous ? -part : part;
   previous = part;
  }
  return value.ToString(System.Globalization.CultureInfo.InvariantCulture) + number.Groups["suffix"].Value;
 }

 internal static string SpeechText(Passage passage, out string[] spokenWords) {
  var words = (string[])passage.Words.Clone();
  int chapters = 0;
  for (int i = 1; i < words.Length; i++)
   if (ChapterNumber(words[i-1], words[i]) != null) chapters++;
  if (chapters >= 2)
   for (int i = 0; i < words.Length; i++)
    if (!Initials.IsMatch(words[i]) && words[i] == words[i].ToUpperInvariant()) words[i] = words[i].ToLowerInvariant();
  for (int i = 0; i < words.Length; i++) {
   Match chapter = i+1 < words.Length ? ChapterNumber(passage.Words[i], passage.Words[i+1]) : null;
   if (chapter != null) {
    if (chapters >= 2 && i > 0 && !IsSentenceEnd(words[i-1]) && !Initials.IsMatch(passage.Words[i-1]))
     words[i-1] += ".";
    words[i] = "Chapter";
    words[i+1] = SpokenChapterNumber(chapter);
    i++;
    continue;
   }
   Match initial = Initials.Match(passage.Words[i]);
   if (!initial.Success) continue;
   string letters = initial.Groups["letters"].Value;
   if (letters.Length == 2 &&
       !(i > 0 && Initials.IsMatch(passage.Words[i-1]) ||
         i+1 < words.Length && Initials.IsMatch(passage.Words[i+1]))) continue;
   words[i] = initial.Groups["prefix"].Value +
    String.Join(" ", letters.Where(c => c != '.').Select(c => LetterNames[c-'A'])) +
    initial.Groups["suffix"].Value;
  }
  int index = 0;
  spokenWords = words;
  return WordPattern.Replace(passage.Text, _ => words[index++]);
 }

 public sealed class Timing {
  public int duration { get; set; }
  public int[][] words { get; set; }
 }

 internal struct Cue {
  public int Word;
  public double Phase;
 }

 internal sealed class Clip {
  public Passage Passage;
  public Timing Timing;
  public string Wave;
  public byte[] WarmPcm;
  public Cue At(int position) {
   int low = 0, high = Timing.words.Length;
   while (low < high) {
    int middle = low + (high - low) / 2;
    if (Timing.words[middle][1] <= position) low = middle + 1;
    else high = middle;
   }
   int[] word = Timing.words[Math.Max(0, low - 1)];
   double phase = Math.Max(0, Math.Min(1, (double)(position - word[1]) / (word[2] - word[1])));
   return new Cue { Word = Passage.Starts[word[0]], Phase = phase };
  }
 }

 readonly string home, tools, unsavedCache, python, voice;
 readonly NarrationAudio audio;
 readonly NarrationWorkerPacer workerPacer = new NarrationWorkerPacer();
 readonly List<Clip> clips = new List<Clip>();
 readonly HashSet<int> readyWords = new HashSet<int>();
 readonly HashSet<int> loadingWords = new HashSet<int>();
 Clip activeClip;
 CancellationTokenSource preparing;
 int playing = -1, pausedWord = -1, pausedAnchor = -1;
 int lastAudioPosition;
 int commaPause = -1, sentencePause = -1;
 bool paused, disposed;

 public ReaderNarration(string home, string area, string python, bool silentCheck = false, string cacheDirectory = null) {
  this.home = home;
  this.python = python;
  audio = new NarrationAudio(silentCheck);
  tools = Path.Combine(area, "Tools");
  unsavedCache = cacheDirectory ?? Path.Combine(area, "Bleeding-Edge", "Phonon-Narration", "Cache");
  string customVoice = Path.Combine(tools, "Phonon-31M", "Breeze-Voice.safetensors");
  if (File.Exists(customVoice)) voice = customVoice;
 }

 public event Action Changed;
 public event Action WordStateChanged;
 public bool Preparing { get { return preparing != null; } }
 public string Status { get; private set; }
 public int PreparedEnd { get { return clips.Count == 0 ? -1 : clips[clips.Count - 1].Passage.End; } }
 public bool NeedsLookahead {
  get {
   if (playing < 0 || paused) return false;
   int remaining = Math.Max(0, audio.Length - lastAudioPosition);
   for (int i = playing + 1; i < clips.Count && remaining < LookaheadMilliseconds; i++)
    remaining += clips[i].Timing.duration;
   return remaining < LookaheadMilliseconds;
  }
 }
 public bool IsWordReady(int start) { return readyWords.Contains(start); }
 public bool IsWordLoading(int start) { return loadingWords.Contains(start); }
 public void ObserveVisualTiming(double frameGapMs, double frameWorkMs) {
  if (Preparing) workerPacer.Observe(frameGapMs, frameWorkMs);
 }

 void PublishWordStates() { if (WordStateChanged != null) WordStateChanged(); }
 void ClearLoadingWords() {
  if (loadingWords.Count == 0) return;
  loadingWords.Clear();
  PublishWordStates();
 }

 public void Configure(int bass, int mids, int treble, int comma, int sentence) {
  audio.SetEqualizer(bass, mids, treble);
  commaPause = comma;
  sentencePause = sentence;
 }

 public void Report(string status) {
  Status = status;
  if (Changed != null) Changed();
 }

 internal static Passage NextPassage(string text, int offset, bool truncated) {
  var matches = WordPattern.Matches(text).Cast<Match>().ToList();
  if (truncated && matches.Count != 0 && !Char.IsWhiteSpace(text[text.Length - 1]))
   matches.RemoveAt(matches.Count - 1);
  if (matches.Count == 0) return null;
  int start = matches[0].Index, end = 0, chapters = 0;
  bool chapterBoundary = false;
  int limit = Math.Min(matches.Count, 42);
  for (int i = 0; i < limit; i++) {
   Match word = matches[i];
   if (i > 0 && word.Index + word.Length - start > 360) break;
   if (i+1 < matches.Count && ChapterNumber(word.Value, matches[i+1].Value) != null && ++chapters > 2) {
    chapterBoundary = true;
    break;
   }
   end = i + 1;
   bool paragraph = end < matches.Count && LineBreak.Matches(text.Substring(word.Index + word.Length,
    matches[end].Index - word.Index - word.Length)).Count >= 2;
   if (end >= 16 && paragraph) break;
   if (end >= 26 && IsSentenceEnd(word.Value)) break;
  }
  // Prefer a complete sentence over a cut at the word or character limit.
  if (!chapterBoundary && end < matches.Count && !IsSentenceEnd(matches[end - 1].Value)) {
   for (int i = end - 2; i >= 15; i--) {
    if (!IsSentenceEnd(matches[i].Value)) continue;
    end = i + 1;
    break;
   }
  }
  // Keep initials attached to their surname at a size boundary.
  while (end < matches.Count && Initials.IsMatch(matches[end-1].Value) &&
         end < 48 && matches[end].Index+matches[end].Length-start <= 420) end++;
  if (end < matches.Count && ChapterNumber(matches[end-1].Value, matches[end].Value) != null) end++;
  var words = matches.GetRange(0, end);
  int finish = words.Last().Index + words.Last().Length;
  return new Passage {
   Text = text.Substring(start, finish - start),
   Starts = words.Select(w => offset + w.Index).ToArray(),
   Words = words.Select(w => w.Value).ToArray(),
   End = offset + finish
  };
 }

 public async void Prepare(List<Passage> passages, string documentPath, bool append, Action<bool> finished) {
  if (disposed || Preparing) return;
  if (passages.Count == 0) { Report("No readable passage here."); return; }
  var request = new CancellationTokenSource();
  preparing = request;
  foreach (var passage in passages) loadingWords.UnionWith(passage.Starts);
  PublishWordStates();
  int published = 0;
  Action<Clip> publish = clip => {
   if (preparing != request) return;
   if (!append && published == 0) { StopAudio(); clips.Clear(); readyWords.Clear(); }
   if (clips.Count - Math.Max(0, playing) >= 6) clip.WarmPcm = null;
   clips.Add(clip);
   loadingWords.ExceptWith(clip.Passage.Starts);
   foreach (var word in clip.Timing.words) readyWords.Add(clip.Passage.Starts[word[0]]);
   published++;
   PublishWordStates();
  };
  var available = new Progress<Clip>(publish);
  var progress = new Progress<string>(message => {
   if (preparing == request) Report(playing >= 0 && !paused ? "Playing · " + message : message);
  });
  Report(playing >= 0 && !paused ? "Playing · Preparing ahead…" : "Preparing with Phonon…");
  try {
   var result = await Task.Run(() => Build(passages, documentPath, progress, available, request.Token));
   if (preparing != request) return;
   foreach (var clip in result.Skip(published)) publish(clip);
   preparing = null;
   ClearLoadingWords();
   Report(String.Format(playing >= 0 && !paused ? "Playing · {0:0} more seconds ready" : "Ready · {0:0} seconds of narration",
    (append && playing < 0 ? clips : result).Sum(c => c.Timing.duration) / 1000.0));
   if (finished != null) finished(true);
  } catch (OperationCanceledException) {
   if (preparing == request) { preparing = null; ClearLoadingWords(); Report("Preparation canceled."); }
  } catch (Exception e) {
   if (preparing == request) { preparing = null; ClearLoadingWords(); Report(e.Message); if (finished != null) finished(false); }
  } finally {
   request.Dispose();
  }
 }

 public void CancelPreparation() {
  var request = preparing;
  if (request == null) return;
  preparing = null;
  request.Cancel();
  ClearLoadingWords();
  Report("Preparation canceled.");
 }

 List<Clip> Build(List<Passage> passages, string documentPath, IProgress<string> progress, IProgress<Clip> available, CancellationToken cancel) {
  string cache = String.IsNullOrEmpty(documentPath) ? unsavedCache :
   Path.Combine(Path.GetDirectoryName(documentPath), "GenAudio", Path.GetFileName(documentPath));
  string phonon = Path.Combine(tools, "Phonon-31M");
  string cli = Path.Combine(phonon, "Runtime", "ptts-0.6.0-x86_64-pc-windows-msvc", "ptts.exe");
  string model = Path.Combine(phonon, "Model");
  string voiceFile = voice ?? Path.Combine(model, "default-voice.safetensors");
  string aligner = Path.Combine(home, "narration_align.py");
  string alignmentModel = Path.Combine(tools, "Breeze-TTS-2", "wav2vec2-base-960h-int8.onnx");
  string vocab = Path.Combine(tools, "Breeze-TTS-2", "wav2vec2-base-960h-vocab.json");
  if (new[] { cli, Path.Combine(model, "model.q8.gguf"), python, aligner, alignmentModel, vocab, voiceFile }.Any(path => !File.Exists(path)))
   throw new InvalidOperationException("Phonon narration files are missing.");
  var serializer = new JavaScriptSerializer();
  var result = new List<Clip>();
  string recipe;
  using (var sha = SHA256.Create())
   recipe = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(voiceFile))) +
    Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(aligner)));
  WorkerGate.Wait(cancel);
  try {
   try { Directory.CreateDirectory(cache); }
   catch (UnauthorizedAccessException) { throw new InvalidOperationException("Cannot write narration to " + cache); }
   catch (IOException error) { throw new InvalidOperationException("Cannot create the narration folder: " + error.Message); }
   for (int index = 0; index < passages.Count; index++) {
    cancel.ThrowIfCancellationRequested();
    Passage passage = passages[index];
    string[] spokenWords;
    string speech = SpeechText(passage, out spokenWords);
    string cacheText = speech == passage.Text ? passage.Text : passage.Text + "\nspeech-v1\n" + speech;
    string key;
    using (var sha = SHA256.Create())
     key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("phonon-ptts0.6.0-cpu-q8-seed42\n" +
      ModelRevision + "\n" + recipe + "\n" + cacheText))).Replace("-", "");
    string folder = Path.Combine(cache, key);
    Clip clip = ReadClip(folder, passage, serializer);
    if (clip == null) {
     progress.Report(String.Format("Preparing with Phonon · {0} of {1}", index + 1, passages.Count));
     string working = Path.Combine(cache, "work-" + Guid.NewGuid().ToString("N"));
     Directory.CreateDirectory(working);
     string wav = Path.Combine(working, "audio.wav");
     string words = Path.Combine(working, "transcript.json");
     string timing = Path.Combine(working, "timing.json");
     var arguments = new List<string> { "--model", model, "--lang", "en", "--quant", "q8", "--device", "cpu",
      "--threads", "2", "--seed", "42", "--output", wav };
     if (voice != null) arguments.AddRange(new[] { "--voice", voice });
     arguments.AddRange(new[] { "--", speech });
     Run(cli, cancel, arguments.ToArray());
     File.WriteAllText(words, serializer.Serialize(spokenWords), new UTF8Encoding(false));
     progress.Report(String.Format("Matching words to audio · {0} of {1}", index + 1, passages.Count));
     Run(python, cancel, "-I", aligner, wav, words, alignmentModel, vocab, timing);
     if (ReadClip(working, passage, serializer) == null) throw new InvalidOperationException("Phonon did not produce a usable audio timeline.");
     cancel.ThrowIfCancellationRequested();
     if (!Directory.Exists(folder)) {
      try { Directory.Move(working, folder); }
      catch (IOException) { if (!Directory.Exists(folder)) throw; }
     }
     clip = ReadClip(folder, passage, serializer) ?? ReadClip(working, passage, serializer);
    }
    if (clip == null) throw new InvalidOperationException("Prepared narration files are unavailable.");
    clip.WarmPcm = NarrationAudio.LoadPcm(clip.Wave);
    result.Add(clip);
    available.Report(clip);
   }
   return result;
  } finally { WorkerGate.Release(); }
 }

 static Clip ReadClip(string folder, Passage passage, JavaScriptSerializer serializer) {
  string wav = Path.Combine(folder, "audio.wav"), metadata = Path.Combine(folder, "timing.json");
  if (!File.Exists(metadata) || !File.Exists(wav)) return null;
  try {
   var timing = serializer.Deserialize<Timing>(File.ReadAllText(metadata));
   if (timing == null || timing.duration <= 0 || timing.words == null || timing.words.Length == 0 ||
       new FileInfo(wav).Length < timing.duration * 48L) return null;
   int previous = -1, lastEnd = 0;
   foreach (var word in timing.words) {
    if (word == null || word.Length != 3 || word[0] <= previous || word[0] >= passage.Starts.Length ||
        word[1] < lastEnd || word[2] <= word[1] || word[2] > timing.duration) return null;
    previous = word[0];
    lastEnd = word[2];
   }
   return new Clip { Wave = wav, Passage = passage, Timing = timing };
  } catch (IOException) { return null; }
    catch (InvalidOperationException) { return null; }
    catch (ArgumentException) { return null; }
 }

 static string Quote(string argument) {
  // Windows CRT quoting, including literal quotes and a trailing backslash.
  return "\"" + Regex.Replace(argument, "(\\\\*)(\"|$)", m => m.Groups[1].Value + m.Groups[1].Value +
   (m.Groups[2].Value == "\"" ? "\\\"" : "")) + "\"";
 }

 void Run(string executable, CancellationToken cancel, params string[] arguments) {
  cancel.ThrowIfCancellationRequested();
  var info = new ProcessStartInfo(executable, String.Join(" ", arguments.Select(Quote))) {
   UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
  };
  using (var process = Process.Start(info))
  using (cancel.Register(() => {
   try { if (!process.HasExited) process.Kill(); }
   catch (InvalidOperationException) {}
   catch (System.ComponentModel.Win32Exception) {}
  })) {
   var stdout = process.StandardOutput.ReadToEndAsync();
   var stderr = process.StandardError.ReadToEndAsync();
   workerPacer.WaitForExit(process);
   cancel.ThrowIfCancellationRequested();
   stdout.GetAwaiter().GetResult();
   string errors = stderr.GetAwaiter().GetResult();
   if (process.ExitCode != 0) {
    string detail = errors.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    if (String.IsNullOrWhiteSpace(detail)) detail = "worker exited with code " + process.ExitCode;
    throw new InvalidOperationException("Narration: " + (detail.Length > 190 ? detail.Substring(0, 190) : detail));
   }
  }
 }

 internal static void ConfigureWorker(Process process) {
  try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
  catch (InvalidOperationException) {}
  catch (System.ComponentModel.Win32Exception) {}

  // Limit native inference pools as well as the CLI thread count. Keep the
  // normal Windows scheduler policy on machines with few available processors.
  try {
   ulong allowed = unchecked((ulong)process.ProcessorAffinity.ToInt64());
   int count = 0;
   for (ulong bits = allowed; bits != 0; bits &= bits - 1) count++;
   if (count < 4) return;
   int remaining = count >= 8 ? 4 : 2;
   ulong selected = 0;
   for (int bit = 63; bit >= 0 && remaining > 0; bit--) {
    ulong candidate = 1UL << bit;
    if ((allowed & candidate) == 0) continue;
    selected |= candidate;
    remaining--;
    bit--;
   }
   for (ulong spare = allowed & ~selected; remaining > 0; spare &= spare - 1, remaining--)
    selected |= spare & ~(spare - 1);
   process.ProcessorAffinity = new IntPtr(unchecked((long)selected));
  } catch (InvalidOperationException) {}
    catch (System.ComponentModel.Win32Exception) {}
 }

 public bool HasWord(int word) {
  return clips.Any(clip => Array.BinarySearch(clip.Passage.Starts, word) >= 0);
 }

 public bool Play(int word, int anchor) {
  try {
   if (paused && pausedWord == word && pausedAnchor == anchor) {
    audio.Resume();
    paused = false;
   } else {
    int index = clips.FindIndex(candidate => Array.BinarySearch(candidate.Passage.Starts, word) >= 0);
    if (index < 0) return false;
    Clip clip = clips[index];
    int[] cue = clip.Timing.words.FirstOrDefault(w => clip.Passage.Starts[w[0]] >= word);
    if (cue == null) return false;
    StartClip(index, word == clip.Passage.Starts[0] ? -1 : Array.IndexOf(clip.Timing.words, cue));
   }
   Report("Playing · Phonon");
   return true;
  } catch (InvalidOperationException error) {
   StopAudio();
   Report(error.Message);
   return false;
  }
 }

 void StartClip(int index, int cueIndex) {
  Clip clip = clips[index];
  activeClip = new Clip { Passage = clip.Passage, Timing = audio.Open(clip, commaPause, sentencePause), Wave = clip.Wave };
  lastAudioPosition = cueIndex < 0 ? 0 : activeClip.Timing.words[cueIndex][1];
  audio.Play(lastAudioPosition);
  int prune = Math.Max(0, index - 1);
  playing = index - prune;
  paused = false;
  if (prune > 0) {
   for (int i = 0; i < prune; i++)
    foreach (var word in clips[i].Timing.words) readyWords.Remove(clips[i].Passage.Starts[word[0]]);
   clips.RemoveRange(0, prune);
   PublishWordStates();
  }
 }

 public void Pause(int word, int anchor) {
  if (playing < 0 || paused) return;
  try {
   audio.Pause();
   paused = true;
   pausedWord = word;
   pausedAnchor = anchor;
   Report("Narration paused.");
  } catch (InvalidOperationException error) {
   StopAudio();
   Report(error.Message);
  }
 }

 public bool Read(out Cue cue, out int end) {
  cue = new Cue();
  end = -1;
  if (playing < 0 || paused) return false;
  try {
   int position = lastAudioPosition = audio.Position;
   if (audio.Ended) {
    if (playing + 1 < clips.Count) {
     StartClip(playing + 1, -1);
     position = 0;
    } else {
     end = clips[playing].Passage.End;
     StopAudio();
     return false;
    }
   }
   cue = activeClip.At(position);
   return true;
  } catch (InvalidOperationException error) {
   StopAudio();
   Report(error.Message);
   return false;
  }
 }

 void StopAudio() {
  audio.Dispose();
  activeClip = null;
  playing = -1;
  paused = false;
 }

 public void Reset() {
  CancelPreparation();
  StopAudio();
  clips.Clear();
  readyWords.Clear();
  PublishWordStates();
  Report(null);
 }

 public void Dispose() {
  disposed = true;
  Reset();
 }
}

sealed class NarrationWorkerPacer {
 const double LateFrameGapMs = 45, SlowFrameWorkMs = 22;
 static readonly long RecoveryTicks = Stopwatch.Frequency / 2;
 long backOffUntil;

 internal void Observe(double frameGapMs, double frameWorkMs) {
  if (frameGapMs >= LateFrameGapMs || frameWorkMs >= SlowFrameWorkMs)
   Interlocked.Exchange(ref backOffUntil, Stopwatch.GetTimestamp() + RecoveryTicks);
 }

 internal void WaitForExit(Process process) {
  ReaderNarration.ConfigureWorker(process);
  ulong ordinaryMask = 0;
  try { ordinaryMask = unchecked((ulong)process.ProcessorAffinity.ToInt64()); }
  catch (InvalidOperationException) {}
  catch (System.ComponentModel.Win32Exception) {}
  ulong oneProcessor = ordinaryMask & ~(ordinaryMask - 1);
  bool backedOff = false;
  do {
   bool late = Stopwatch.GetTimestamp() < Interlocked.Read(ref backOffUntil);
   if (late == backedOff) continue;
   backedOff = late;
   try { process.PriorityClass = late ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal; }
   catch (InvalidOperationException) {}
   catch (System.ComponentModel.Win32Exception) {}
   if (ordinaryMask != 0 && ordinaryMask != oneProcessor) {
    try { process.ProcessorAffinity = new IntPtr(unchecked((long)(late ? oneProcessor : ordinaryMask))); }
    catch (InvalidOperationException) {}
    catch (System.ComponentModel.Win32Exception) {}
   }
  } while (!process.WaitForExit(35));
 }
}

sealed class NarrationAudio : IDisposable {
 internal const uint SampleRate = 24000;
 const uint BytesPerSample = 2;
 const uint TimeMilliseconds = 1, TimeSamples = 2, TimeBytes = 4, HeaderDone = 1;
 const uint CallbackEvent = 0x50000;
 const int BufferSize = (int)(SampleRate * BytesPerSample * 32 / 1000);

 [StructLayout(LayoutKind.Sequential, Pack = 2)]
 struct WaveFormat {
  public ushort FormatTag, Channels;
  public uint SamplesPerSecond, BytesPerSecond;
  public ushort BlockAlign, BitsPerSample, ExtraSize;
 }

 [StructLayout(LayoutKind.Sequential)]
 struct WaveHeader {
  public IntPtr Data;
  public uint BufferLength, BytesRecorded;
  public UIntPtr User;
  public uint Flags, Loops;
  public IntPtr Next;
  public UIntPtr Reserved;
 }

 [StructLayout(LayoutKind.Sequential)]
 struct WaveTime { public uint Type, Value, Reserved; }

 [DllImport("winmm.dll")]
 static extern int waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
 [DllImport("winmm.dll")]
 static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
 [DllImport("winmm.dll")]
 static extern int waveOutWrite(IntPtr handle, IntPtr header, uint size);
 [DllImport("winmm.dll")]
 static extern int waveOutGetPosition(IntPtr handle, ref WaveTime time, uint size);
 [DllImport("winmm.dll")]
 static extern int waveOutPause(IntPtr handle);
 [DllImport("winmm.dll")]
 static extern int waveOutRestart(IntPtr handle);
 [DllImport("winmm.dll")]
 static extern int waveOutReset(IntPtr handle);
 [DllImport("winmm.dll")]
 static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
 [DllImport("winmm.dll")]
 static extern int waveOutClose(IntPtr handle);
 [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
 static extern int waveOutGetErrorText(int code, StringBuilder text, uint capacity);

 static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(WaveHeader));
 static readonly int FlagsOffset = Marshal.OffsetOf(typeof(WaveHeader), "Flags").ToInt32();
 static readonly int BufferLengthOffset = Marshal.OffsetOf(typeof(WaveHeader), "BufferLength").ToInt32();

 sealed class OutputBuffer {
  public readonly byte[] Bytes = new byte[BufferSize];
  public GCHandle Samples;
  public IntPtr Header;
  public bool Prepared, Queued;
 }

 readonly OutputBuffer[] buffers = { new OutputBuffer(), new OutputBuffer(), new OutputBuffer() };
 readonly bool silentCheck;
 IntPtr device;
 AutoResetEvent signal;
 Thread pump;
 byte[] pcm;
 NarrationEqualizer filter;
 volatile bool stopping, ended;
 volatile InvalidOperationException failure;
 int offsetMilliseconds, nextByte;
 volatile int eqSettings = NarrationEqualizer.Pack(0, 0, 0);
 internal volatile int AppliedEqualizer;
 public int Length { get; private set; }

 public NarrationAudio(bool silentCheck = false) { this.silentCheck = silentCheck; }

 public void SetEqualizer(int bass, int mids, int treble) {
  eqSettings = NarrationEqualizer.Pack(bass, mids, treble);
 }

 static void Check(int code) {
  if (code == 0) return;
  var result = new StringBuilder(256);
  waveOutGetErrorText(code, result, (uint)result.Capacity);
  throw new InvalidOperationException("Narration audio: " + (result.Length == 0 ? "Windows error " + code : result.ToString()));
 }

 static byte[] ReadPcm(string path) {
  using (var reader = new BinaryReader(File.OpenRead(path))) {
   if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException();
   long end = 8L + reader.ReadUInt32();
   if (end < 12 || end > reader.BaseStream.Length || reader.ReadUInt32() != 0x45564157)
    throw new InvalidDataException();
   bool format = false;
   byte[] data = null;
   while (reader.BaseStream.Position + 8 <= end) {
    uint chunk = reader.ReadUInt32(), size = reader.ReadUInt32();
    long next = reader.BaseStream.Position + size + (size & 1);
    if (next > end) throw new InvalidDataException();
    if (chunk == 0x20746d66 && size >= 16) {
     format = reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1 &&
      reader.ReadUInt32() == SampleRate && reader.ReadUInt32() == SampleRate * BytesPerSample &&
      reader.ReadUInt16() == BytesPerSample && reader.ReadUInt16() == 16;
    } else if (chunk == 0x61746164 && size > 0 && size % BytesPerSample == 0 && size <= Int32.MaxValue) {
     data = reader.ReadBytes((int)size);
    }
    if (format && data != null) return data;
    reader.BaseStream.Position = next;
   }
   throw new InvalidDataException();
  }
 }

 internal static byte[] LoadPcm(string path) {
  try { return ReadPcm(path); }
  catch (InvalidDataException) { throw new InvalidOperationException("Narration audio is not a supported PCM WAV."); }
  catch (IOException) { throw new InvalidOperationException("Narration audio could not read the prepared WAV."); }
  catch (UnauthorizedAccessException) { throw new InvalidOperationException("Narration audio could not access the prepared WAV."); }
 }

 void Open(string path, ReaderNarration.Clip clip, int comma, int sentence, out ReaderNarration.Timing timing) {
  Dispose();
  pcm = clip == null ? null : clip.WarmPcm;
  if (clip != null) clip.WarmPcm = null;
  if (pcm == null) pcm = LoadPcm(path);
  timing = clip == null ? null : NarrationPauses.Apply(ref pcm, clip, comma, sentence);
  Length = (int)Math.Ceiling(1000.0 * pcm.Length / (SampleRate * BytesPerSample));
 }

 public void Open(string path) {
  ReaderNarration.Timing unused;
  Open(path, null, -1, -1, out unused);
 }

 public ReaderNarration.Timing Open(ReaderNarration.Clip clip, int comma, int sentence) {
  ReaderNarration.Timing timing;
  Open(clip.Wave, clip, comma, sentence, out timing);
  return timing;
 }

 public void Play(int from) {
  if (pcm == null || !ReleaseDevice() || from < 0 || from >= Length)
   throw new InvalidOperationException("Narration audio could not start playback.");
  offsetMilliseconds = from;
  nextByte = (int)(from * (long)SampleRate / 1000 * BytesPerSample);
  failure = null;
  ended = stopping = false;
  filter = new NarrationEqualizer();
  var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSecond = SampleRate,
   BytesPerSecond = SampleRate * BytesPerSample, BlockAlign = (ushort)BytesPerSample, BitsPerSample = 16 };
  signal = new AutoResetEvent(false);
  Check(waveOutOpen(out device, UInt32.MaxValue, ref format, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent));
  foreach (var buffer in buffers) {
   buffer.Samples = GCHandle.Alloc(buffer.Bytes, GCHandleType.Pinned);
   buffer.Header = Marshal.AllocHGlobal((int)HeaderSize);
   Marshal.StructureToPtr(new WaveHeader {
    Data = buffer.Samples.AddrOfPinnedObject(), BufferLength = BufferSize
   }, buffer.Header, false);
   Check(waveOutPrepareHeader(device, buffer.Header, HeaderSize));
   buffer.Prepared = true;
  }
  foreach (var buffer in buffers) Queue(buffer);
  pump = new Thread(Refill) { IsBackground = true, Name = "PageCraft narration output", Priority = ThreadPriority.AboveNormal };
  pump.Start();
 }

 void Queue(OutputBuffer buffer) {
  int count = Math.Min(BufferSize, pcm.Length - nextByte);
  if (count <= 0) return;
  int settings = eqSettings;
  filter.Fill(pcm, nextByte, buffer.Bytes, count, settings);
  if (silentCheck) Array.Clear(buffer.Bytes, 0, count);
  Marshal.WriteInt32(buffer.Header, BufferLengthOffset, count);
  Check(waveOutWrite(device, buffer.Header, HeaderSize));
  AppliedEqualizer = settings;
  nextByte += count;
  buffer.Queued = true;
 }

 void Refill() {
  try {
   while (!stopping) {
    signal.WaitOne();
    if (stopping) return;
    bool pending = false;
    // Refill in submission order when more than one buffer has completed.
    for (int count = 0; count < buffers.Length; count++) {
     var buffer = buffers[refillIndex];
     if (!buffer.Queued) break;
     if (((uint)Marshal.ReadInt32(buffer.Header, FlagsOffset) & HeaderDone) == 0) break;
     buffer.Queued = false;
     refillIndex = (refillIndex + 1) % buffers.Length;
     Queue(buffer);
    }
    foreach (var buffer in buffers) pending |= buffer.Queued;
    if (!pending) { ended = true; return; }
   }
  } catch (InvalidOperationException error) { failure = error; }
 }

 int refillIndex;

 public void Pause() { if (!Ended) Check(waveOutPause(device)); }
 public void Resume() { if (!Ended) Check(waveOutRestart(device)); }
 public int Position {
  get {
   if (failure != null) throw failure;
   if (Ended) return Length;
   if (device == IntPtr.Zero) return 0;
   var time = new WaveTime { Type = TimeSamples };
   Check(waveOutGetPosition(device, ref time, (uint)Marshal.SizeOf(typeof(WaveTime))));
   long elapsed;
   switch (time.Type) {
    case TimeSamples: elapsed = time.Value * 1000L / SampleRate; break;
    case TimeBytes: elapsed = time.Value * 1000L / (SampleRate * BytesPerSample); break;
    case TimeMilliseconds: elapsed = time.Value; break;
    default: throw new InvalidOperationException("Narration audio clock is unavailable.");
   }
   return (int)Math.Min(Length, offsetMilliseconds + elapsed);
  }
 }
 public bool Ended { get { return ended; } }

 bool ReleaseDevice() {
  if (pump != null) {
   stopping = true;
   signal.Set();
   pump.Join();
   pump = null;
  }
  if (device != IntPtr.Zero) waveOutReset(device);
  foreach (var buffer in buffers) {
   // Windows must release a header before its memory or pinned samples are freed.
   if (buffer.Prepared && waveOutUnprepareHeader(device, buffer.Header, HeaderSize) != 0) return false;
   buffer.Prepared = buffer.Queued = false;
   if (buffer.Header != IntPtr.Zero) { Marshal.FreeHGlobal(buffer.Header); buffer.Header = IntPtr.Zero; }
   if (buffer.Samples.IsAllocated) buffer.Samples.Free();
  }
  if (device != IntPtr.Zero) {
   if (waveOutClose(device) != 0) return false;
   device = IntPtr.Zero;
  }
  if (signal != null) { signal.Dispose(); signal = null; }
  refillIndex = 0;
  return true;
 }

 public void Dispose() { ReleaseDevice(); pcm = null; Length = 0; }
}
