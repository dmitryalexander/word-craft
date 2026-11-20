using System;
using System.IO;
using System.Text.RegularExpressions;

sealed class NarrationEqualizer {
 sealed class Band {
  readonly double cosine, alpha;
  double gain, b0 = 1, b1, b2, a1, a2, z1, z2;
  public Band(int frequency) {
   double angle = 2 * Math.PI * frequency / NarrationAudio.SampleRate;
   cosine = Math.Cos(angle);
   alpha = Math.Sin(angle) / 1.4;
   SetCoefficients(0);
  }
  void SetCoefficients(double decibels) {
   gain = decibels;
   double a = Math.Pow(10, gain / 40), divisor = 1 + alpha / a;
   b0 = (1 + alpha * a) / divisor;
   b1 = a1 = -2 * cosine / divisor;
   b2 = (1 - alpha * a) / divisor;
   a2 = (1 - alpha / a) / divisor;
  }
  public void Approach(int target, bool first) {
   if (gain == target) return;
   double next = gain + (target - gain) * .45;
   SetCoefficients(first || Math.Abs(next - target) < .02 ? target : next);
  }
  public double Apply(double sample) {
   double output = b0 * sample + z1;
   z1 = b1 * sample - a1 * output + z2;
   z2 = b2 * sample - a2 * output;
   return output;
  }
 }

 readonly Band bass = new Band(180), mids = new Band(1200), treble = new Band(4500);
 double level = 1;
 bool first = true;

 public void Fill(byte[] source, int offset, byte[] destination, int count, int settings) {
  int low = (settings & 31) - 12, mid = ((settings >> 5) & 31) - 12, high = ((settings >> 10) & 31) - 12;
  bass.Approach(low, first); mids.Approach(mid, first); treble.Approach(high, first);
  int boost = Math.Max(low, Math.Max(mid, high));
  double target = Math.Pow(10, -Math.Max(0, boost - 3) / 20.0);
  double change = first ? 0 : (target - level) / (count / 2);
  if (first) level = target;
  for (int index = 0; index < count; index += 2) {
   double sample = (short)(source[offset + index] | (source[offset + index + 1] << 8));
   level += change;
   int output = (int)Math.Round(treble.Apply(mids.Apply(bass.Apply(sample))) * level);
   output = Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, output));
   destination[index] = (byte)output;
   destination[index + 1] = (byte)(output >> 8);
  }
  level = target;
  first = false;
 }

 public static int Pack(int bass, int mids, int treble) {
  return (bass + 12) | ((mids + 12) << 5) | ((treble + 12) << 10);
 }
}

static class NarrationPauses {
 const int SamplesPerMs = (int)NarrationAudio.SampleRate / 1000;
 const int Fade = 3 * SamplesPerMs;
 static readonly Regex Comma = new Regex("[,][\"'”’»)}\\]]*$");

 static int Sample(byte[] pcm, int index) {
  return (short)(pcm[index * 2] | (pcm[index * 2 + 1] << 8));
 }

 static void FadeEdge(byte[] pcm, int point, int direction) {
  for (int index = 0; index < Fade; index++) {
   int sample = direction < 0 ? point - index - 1 : point + index;
   int value = Sample(pcm, sample) * index / Fade;
   pcm[sample * 2] = (byte)value;
   pcm[sample * 2 + 1] = (byte)(value >> 8);
  }
 }

 internal static ReaderNarration.Timing Apply(ref byte[] pcm, ReaderNarration.Clip clip, int comma, int sentence) {
  var timing = clip.Timing;
  if (comma < 0 && sentence < 0) return timing;
  int length = pcm.Length / 2, peak = 0;
  for (int index = 0; index < length; index++) peak = Math.Max(peak, Math.Abs(Sample(pcm, index)));
  int threshold = Math.Max(100, peak / 20);
  int shift = 0, copied = 0;
  var words = new int[timing.words.Length][];
  using (var edited = new MemoryStream(pcm.Length)) {
   for (int index = 0; index < words.Length; index++) {
    int[] word = timing.words[index];
    words[index] = new[] { word[0], word[1] + shift, word[2] + shift };
    string token = clip.Passage.Words[word[0]];
    int target = Comma.IsMatch(token) ? comma : ReaderNarration.IsSentenceEnd(token) ? sentence : -1;
    if (target < 0) continue;
    int next = index + 1 < words.Length ? timing.words[index + 1][1] : timing.duration;
    int wanted = target - (next - word[2]);
    if (wanted == 0) continue;
    int begin = Math.Max(copied, word[2] * SamplesPerMs);
    int end = Math.Min(length, next * SamplesPerMs);
    int quietStart = begin, run = begin, quietLength = 0;
    for (int sample = begin; sample < end; sample++) {
     if (Math.Abs(Sample(pcm, sample)) > threshold) run = sample + 1;
     else if (sample + 1 - run > quietLength) { quietStart = run; quietLength = sample + 1 - run; }
    }
    int removable = (quietLength - Fade * 2) / SamplesPerMs;
    if (removable < 1) continue;
    int remove = Math.Min(removable, Math.Max(0, -wanted)) * SamplesPerMs;
    int insert = Math.Max(0, wanted) * SamplesPerMs;
    int start = quietStart + (quietLength - remove) / 2;
    FadeEdge(pcm, start, -1);
    FadeEdge(pcm, start + remove, 1);
    edited.Write(pcm, copied * 2, (start - copied) * 2);
    if (insert > 0) edited.Write(new byte[insert * 2], 0, insert * 2);
    copied = start + remove;
    shift += (insert - remove) / SamplesPerMs;
   }
   if (copied == 0) return timing;
   edited.Write(pcm, copied * 2, pcm.Length - copied * 2);
   pcm = edited.ToArray();
  }
  return new ReaderNarration.Timing { duration = timing.duration + shift, words = words };
 }
}
