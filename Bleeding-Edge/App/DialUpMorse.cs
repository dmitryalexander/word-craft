using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

sealed class DialUpMorse : IDisposable
{
    const double DraftDot = .022;
    const double MinimumDraftDot = .010;
    const double MinimumLetterSilence = .120;
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,?'!/()&:;=+-_\"$@";
    static readonly string[] Codes = (
        ".- -... -.-. -.. . ..-. --. .... .. .--- -.- .-.. -- -. --- .--. --.- .-. ... - ..- ...- .-- -..- -.-- --.. " +
        "----- .---- ..--- ...-- ....- ..... -.... --... ---.. ----. " +
        ".-.-.- --..-- ..--.. .----. -.-.-- -..-. -.--. -.--.- .-... ---... -.-.-. -...- .-.-. -....- ..--.- .-..-. ...-..- .--.-."
    ).Split(' ');

    sealed class Clip
    {
        internal short[] Samples;
        internal int Center;
    }

    readonly Clip[] major;
    readonly Clip[] minor;
    readonly Random random = new Random();
    readonly MorseOutput output;
    readonly JapaneseRomaji romaji = new JapaneseRomaji();
    int previousMajor = -1, previousMinor = -1;
    double lastTypedAt = Double.NegativeInfinity;
    double volume = 1;
    internal double Volume
    {
        get { return volume; }
        set { volume=Math.Max(0,Math.Min(1,value)); if(output!=null)output.Volume=volume; }
    }
    internal void ClearPending() { if(output!=null)output.Replace(new short[0]); }

    internal DialUpMorse(bool enableOutput = true)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DialUpMorse.pcm"))
        using (var reader = new BinaryReader(stream))
        {
            major = ReadBank(reader, 4, 6480, 3);
            minor = ReadBank(reader, 5, 2160, 1);
        }
        if (enableOutput)
            output = new MorseOutput();
    }

    static Clip[] ReadBank(BinaryReader reader, int count, int length, int markLength)
    {
        var bank = new Clip[count];
        int margin = (int)(MorseOutput.SampleRate * DraftDot * markLength) / 2;
        for (int i = 0; i < count; i++)
        {
            var samples = new short[length];
            for (int j = 0; j < length; j++)
                samples[j] = reader.ReadInt16();
            int center = length / 2, bestCenter = center;
            long centerEnergy = WindowEnergy(samples, center), bestEnergy = centerEnergy;
            for (int candidate = margin; candidate <= length - margin; candidate += 24)
            {
                long energy = WindowEnergy(samples, candidate);
                if (energy <= bestEnergy)
                    continue;
                bestEnergy = energy;
                bestCenter = candidate;
            }
            bank[i] = new Clip { Samples = samples, Center = bestEnergy > 4 * centerEnergy ? bestCenter : center };
        }
        return bank;
    }

    static long WindowEnergy(short[] samples, int center)
    {
        const int radius = 144;
        long sum = 0;
        for (int i = center - radius; i < center + radius; i++)
            sum += (long)samples[i] * samples[i];
        return sum;
    }

    internal void Play(string element, double seconds)
    {
        if (output == null || String.IsNullOrWhiteSpace(element))
            return;
        output.Enqueue(Render(element, seconds));
    }

    internal void PlayTyped(string text, double now)
    {
        if (output == null || String.IsNullOrWhiteSpace(text))
            return;
        double interval = Math.Max(.045, Math.Min(.14, now - lastTypedAt));
        lastTypedAt = now;
        output.Replace(RenderTyping(interval));
    }

    internal short[] RenderTyping(double interval)
    {
        // One small gesture per input event, rather than spelling a paste/IME commit.
        const string gestures = "ETIANSMR";
        return RenderLetter(gestures[random.Next(gestures.Length)].ToString(),
            Math.Max(.045, Math.Min(.14, interval)), .009, .003, false);
    }

    internal void PlayFocused(string word, int offset, string element, double seconds)
    {
        if (output == null) return;
        string spoken = JapaneseRomaji.ContainsJapanese(word) ? romaji.At(word, offset) : element;
        output.Enqueue(RenderPlayback(spoken, seconds));
    }

    internal void EndDraft()
    {
        lastTypedAt = Double.NegativeInfinity;
    }

    internal short[] Render(string element, double seconds, double longestDot = .009, double shortestDot = 0)
    {
        string spoken = romaji.Romanize(element);
        var samples = new List<short>();
        var elements = StringInfo.GetTextElementEnumerator(spoken);
        while (elements.MoveNext())
            samples.AddRange(RenderLetter(elements.GetTextElement(), seconds, longestDot, shortestDot, true));
        return samples.ToArray();
    }

    internal short[] RenderPlayback(string text, double seconds)
    {
        var samples = new List<short>();
        var elements = StringInfo.GetTextElementEnumerator(romaji.Romanize(text));
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (!String.IsNullOrWhiteSpace(element))
                samples.AddRange(RenderLetter(element, seconds, .009, 0, false));
        }
        return samples.ToArray();
    }

    short[] RenderLetter(string element, double seconds, double longestDot, double shortestDot, bool separateLetters)
    {
        string code = CodeFor(element);
        if (code.Length == 0)
            return new short[0];
        int marks = 0;
        foreach (char symbol in code) marks += symbol == '-' ? 3 : 1;
        // Fit the signal into the next keystroke interval. Crop a pitched slice of each clip
        // so the original modem pitch survives even at the fastest reply pace.
        int units = marks + code.Length - 1 + 3;
        double dot = Math.Max(shortestDot, Math.Min(longestDot, seconds * .84 / units));
        int unit = Math.Max(16, (int)(MorseOutput.SampleRate * dot));
        int gap = unit;
        // Typing retains clear letter boundaries; reply playback adds no letter or word silence.
        int letterGap = separateLetters ? Math.Max(3 * unit, (int)Math.Ceiling(MorseOutput.SampleRate * MinimumLetterSilence)) : 0;
        var burst = new short[(marks + code.Length - 1) * unit + letterGap];
        int cursor = 0;
        foreach (char symbol in code)
        {
            bool dash = symbol == '-';
            Clip clip = dash ? Pick(major, ref previousMajor) : Pick(minor, ref previousMinor);
            int count = unit * (dash ? 3 : 1);
            int start = clip.Center - count / 2;
            int fade = Math.Max(1, Math.Min(30, count / 4));
            long energy = 0;
            int peak = 0;
            for (int i = 0; i < count; i++)
            {
                double envelope = Math.Min(1, (double)Math.Min(i, count - i - 1) / fade);
                int sample = (int)(clip.Samples[start + i] * envelope);
                burst[cursor + i] = (short)sample;
                energy += (long)sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            // Compare the level of the frames actually played, after the edge fade.
            double rms = Math.Sqrt((double)energy / count);
            double gain = Math.Min(4, Math.Min(2800 / Math.Max(1, rms), 15500.0 / Math.Max(1, peak)));
            for (int i = 0; i < count; i++)
                burst[cursor + i] = (short)(burst[cursor + i] * gain);
            cursor += count + gap;
        }
        return burst;
    }

    Clip Pick(Clip[] bank, ref int previous)
    {
        int choice = random.Next(bank.Length - (previous < 0 ? 0 : 1));
        if (previous >= 0 && choice >= previous)
            choice++;
        previous = choice;
        return bank[choice];
    }

    static string CodeFor(string element)
    {
        if (String.IsNullOrWhiteSpace(element))
            return "";
        // Fold accented letters into the base alphabet. Other printable characters still get a short chirp.
        if (element == "’") element = "'";
        if (element == "“" || element == "”") element = "\"";
        if (element == "—" || element == "–") element = "-";
        string normalized = element.Normalize(NormalizationForm.FormD).ToUpperInvariant();
        int index = Alphabet.IndexOf(normalized[0]);
        return index >= 0 ? Codes[index] : ".";
    }

    public void Dispose()
    {
        romaji.Dispose();
        if (output != null)
            output.Dispose();
    }
}

sealed class MorseOutput : IDisposable
{
    internal const int SampleRate = 24000;
    const int BlockFrames = SampleRate / 50;
    const int HeaderDone = 1;
    const uint CallbackEvent = 0x50000;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct Format
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Header
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    static extern int waveOutOpen(out IntPtr device, uint id, ref Format format, IntPtr callback, IntPtr user, uint flags);
    [DllImport("winmm.dll")]
    static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    static extern int waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")]
    static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    static extern int waveOutClose(IntPtr device);

    static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(Header));
    static readonly int FlagsOffset = Marshal.OffsetOf(typeof(Header), "Flags").ToInt32();

    sealed class Block
    {
        internal readonly short[] Samples = new short[BlockFrames];
        internal GCHandle Pin;
        internal IntPtr Header;
        internal bool Prepared, Submitted;
    }

    readonly object gate = new object();
    readonly LinkedList<short[]> queue = new LinkedList<short[]>();
    readonly AutoResetEvent signal = new AutoResetEvent(false);
    readonly Thread pump;
    int queuedFrames, headOffset;
    int transitionFrames;
    short lastSample;
    double volume = 1, currentVolume = 1;
    internal double Volume
    {
        get { lock(gate)return volume; }
        set { lock(gate)volume=Math.Max(0,Math.Min(1,value)); }
    }
    bool stopped;

    internal MorseOutput(bool enableOutput = true)
    {
        if (!enableOutput) return;
        pump = new Thread(Output) { IsBackground = true, Name = "Chat Craft Morse audio" };
        pump.Start();
    }

    internal void Enqueue(short[] samples)
    {
        if (samples.Length == 0)
            return;
        lock (gate)
        {
            if (stopped)
                return;
            queue.AddLast(samples);
            queuedFrames += samples.Length;
        }
    }

    internal void Replace(short[] samples)
    {
        lock (gate)
        {
            if (stopped) return;
            queue.Clear();
            headOffset = 0;
            queuedFrames = samples.Length;
            if (samples.Length > 0) queue.AddLast(samples);
            transitionFrames = SampleRate / 250; // 4 ms crossfade into the newest gesture.
        }
    }

    int Fill(short[] destination)
    {
        lock (gate)
        {
            int written = 0;
            while (!stopped && written < destination.Length && queue.First != null)
            {
                short[] head = queue.First.Value;
                int count = Math.Min(destination.Length - written, head.Length - headOffset);
                Array.Copy(head, headOffset, destination, written, count);
                for (int i = written; i < written + count && transitionFrames > 0; i++)
                {
                    double blend = 1 - (double)transitionFrames / (SampleRate / 250);
                    destination[i] = (short)(lastSample * (1-blend) + destination[i] * blend);
                    transitionFrames--;
                }
                written += count;
                queuedFrames -= count;
                headOffset += count;
                if (headOffset == head.Length)
                {
                    queue.RemoveFirst();
                    headOffset = 0;
                }
            }
            lastSample = written > 0 ? destination[written-1] : (short)0;
            for(int i=0;i<written;i++)
            {
                currentVolume += Math.Max(-1.0/96,Math.Min(1.0/96,volume-currentVolume));
                destination[i]=(short)(destination[i]*currentVolume);
            }
            return written;
        }
    }

    void Output()
    {
        IntPtr device = IntPtr.Zero;
        var blocks = new[] { new Block(), new Block(), new Block() };
        var format = new Format { Tag = 1, Channels = 1, Rate = SampleRate,
            BytesPerSecond = SampleRate * 2, BlockAlign = 2, Bits = 16 };
        try
        {
            Check(waveOutOpen(out device, UInt32.MaxValue, ref format,
                signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent));
            foreach (Block block in blocks)
            {
                block.Pin = GCHandle.Alloc(block.Samples, GCHandleType.Pinned);
                block.Header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new Header { Data = block.Pin.AddrOfPinnedObject(), Length = BlockFrames * 2 },
                    block.Header, false);
                Check(waveOutPrepareHeader(device, block.Header, HeaderSize));
                block.Prepared = true;
            }
            int next = 0;
            while (true)
            {
                lock (gate)
                {
                    if (stopped)
                        return;
                }
                for (int i = 0; i < blocks.Length; i++)
                {
                    Block block = blocks[next];
                    if (block.Submitted && (Marshal.ReadInt32(block.Header, FlagsOffset) & HeaderDone) == 0)
                        break;
                    int frames = Fill(block.Samples);
                    // Keep the output stream active through idle gaps. These frames are digital silence.
                    Array.Clear(block.Samples, frames, BlockFrames - frames);
                    Check(waveOutWrite(device, block.Header, HeaderSize));
                    block.Submitted = true;
                    next = (next + 1) % blocks.Length;
                }
                signal.WaitOne();
            }
        }
        catch (InvalidOperationException error) { Trace.WriteLine(error.Message); }
        finally
        {
            lock (gate)
            {
                stopped = true;
                queue.Clear();
            }
            if (device != IntPtr.Zero)
                waveOutReset(device);
            foreach (Block block in blocks)
            {
                if (block.Prepared && waveOutUnprepareHeader(device, block.Header, HeaderSize) != 0)
                    continue;
                if (block.Header != IntPtr.Zero)
                    Marshal.FreeHGlobal(block.Header);
                if (block.Pin.IsAllocated)
                    block.Pin.Free();
            }
            if (device != IntPtr.Zero)
                waveOutClose(device);
            signal.Dispose();
        }
    }

    static void Check(int result)
    {
        if (result != 0)
            throw new InvalidOperationException("Chat Craft audio device returned " + result.ToString(CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!stopped)
            {
                stopped = true;
                signal.Set();
            }
        }
        if (pump != null) pump.Join();
        else signal.Dispose();
    }
}
