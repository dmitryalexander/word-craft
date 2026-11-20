using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms.Integration;
using Forms = System.Windows.Forms;

sealed class WordFlowView : WindowsFormsHost
{
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    sealed class NativeSurface : Forms.Panel
    {
        internal Func<Forms.Keys, bool, bool> Shortcut;

        protected override bool ProcessCmdKey(ref Forms.Message message, Forms.Keys key)
        {
            if (Shortcut != null && Shortcut(key, (message.LParam.ToInt64() & 0x40000000) != 0))
                return true;
            return base.ProcessCmdKey(ref message, key);
        }
    }

    readonly NativeSurface surface = new NativeSurface();
    readonly RuledReader source = new RuledReader { ReadOnly = true, Visible = false, TabStop = false };
    readonly KineticReader reader = new KineticReader
    {
        TrackWordHits = true,
        CenterWholeWord = true,
        ReadingBloom = true,
        SlowPlaybackFactor = 0.33,
        Dock = Forms.DockStyle.Fill
    };
    MatchCollection words, sourceWords;
    FlowMarkdown markdown;
    FlowTuningPanel tuning;
    int wheelDelta;
    readonly string defaultsPath;

    public event Action<int> WordClicked;
    public Func<Forms.Keys, bool, bool> Shortcut;
    internal KineticReader Reader { get { return reader; } }
    internal int TextLength { get { return source.TextLength; } }
    internal int CurrentWord { get { return SourceWord(reader.TextAnchor); } }

    public WordFlowView(IntPtr owner, Func<double, System.Drawing.SizeF?> focusPopCurve,
        string profileDirectory = null, bool enableNarration = true)
    {
        string area = profileDirectory ?? Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../.."));
        defaultsPath = Path.Combine(area, "Bleeding-Edge", "Word-Flow-Defaults.json");
        if (enableNarration)
        {
            string pageCraft = Path.GetFullPath(Path.Combine(area, "../PageCraft"));
            string python = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"miniconda3\python.exe");
            reader.SetNarrationEngine(new ReaderNarration(Path.Combine(pageCraft, "Bleeding-Edge", "App"),
                pageCraft, python, cacheDirectory: Path.Combine(area, "Bleeding-Edge", "Phonon-Narration", "Cache")));
        }
        RestoreDefaults();
        reader.SetNarration(false);
        surface.BackColor = System.Drawing.Color.Black;
        surface.Controls.Add(source);
        surface.Controls.Add(reader);
        Child = surface;
        surface.Shortcut = (key, repeat) => Shortcut != null && Shortcut(key, repeat);
        reader.WheelOverride = ApplyWheel;
        reader.FocusPopCurve = focusPopCurve;
        // Flow is hosted across WPF and WinForms, so scope Shift to the foreground window.
        reader.ReadSlowPlaybackModifier = () =>
        {
            IntPtr foreground = GetForegroundWindow();
            if (!IsVisible || foreground == IntPtr.Zero)
                return false;
            bool ownsForeground = foreground == owner ||
                (tuning != null && tuning.IsHandleCreated && foreground == tuning.Handle);
            return ownsForeground && (GetAsyncKeyState((int)Forms.Keys.LShiftKey) < 0 ||
                GetAsyncKeyState((int)Forms.Keys.RShiftKey) < 0);
        };
        reader.MouseClick += (sender, args) =>
        {
            if (args.Button != Forms.MouseButtons.Left)
                return;
            FocusAtPoint(args.Location);
        };
    }

    internal void FocusAtPoint(System.Drawing.Point point)
    {
        int hit = reader.WordAtPoint(point);
        if (hit >= 0 && WordClicked != null)
            WordClicked(SourceWord(hit));
    }

    public void Open(string text, int word, bool takeFocus = true)
    {
        markdown = new FlowMarkdown(text);
        source.Text = markdown.Text;
        words = Regex.Matches(markdown.Text, @"\S+");
        sourceWords = Regex.Matches(text, @"\S+");
        reader.ReadGlyphStyle = markdown.StyleAt;
        wheelDelta = 0;
        reader.ResetSyllableFocus();
        reader.ShowReader(source, DisplayWordOffset(word), takeFocus);
    }

    internal void UpdateReplyText(string text)
    {
        int anchor = markdown.SourceOffset(reader.TextAnchor);
        var next = new FlowMarkdown(text);
        bool appending = next.Text.StartsWith(markdown.Text, StringComparison.Ordinal);
        int previousLength = markdown.Text.Length;
        markdown = next;
        words = Regex.Matches(next.Text, @"\S+");
        sourceWords = Regex.Matches(text, @"\S+");
        reader.ReadGlyphStyle = next.StyleAt;
        if (appending)
        {
            source.AppendText(next.Text.Substring(previousLength));
            reader.RefreshContent();
        }
        else
        {
            source.Text = next.Text;
            reader.Seek(next.VisibleOffset(anchor));
        }
    }

    internal void FocusWord(int word)
    {
        reader.PausePlayback();
        reader.SeekWord(DisplayWordOffset(word));
        reader.PlayFocusPop();
    }

    internal void Reload(string text, int word)
    {
        reader.HideView();
        Open(text, word);
    }

    internal void ShowSelection(int start, int length)
    {
        if (length == 0)
        {
            reader.SetTextSelection(0, 0);
            return;
        }
        reader.SetTextSelection(markdown.VisibleOffset(start), markdown.VisibleOffset(start + length));
    }

    public void Leave()
    {
        if (tuning != null)
            tuning.Hide();
        reader.SetTextSelection(0, 0);
        reader.HideView();
        source.Clear();
        words = null;
        sourceWords = null;
        markdown = null;
        reader.ReadGlyphStyle = null;
    }

    internal void ApplyWheel(int delta, bool control)
    {
        if (words == null || words.Count == 0 || reader.IsSizingEffects)
            return;
        if (control)
        {
            SetZoom((float)(reader.Zoom * Math.Pow(1.12, delta / 120.0)));
            return;
        }
        wheelDelta += delta;
        int movement = wheelDelta / 120;
        wheelDelta %= 120;
        if (movement != 0)
            MoveBySyllables(-movement);
    }

    void MoveBySyllables(int movement)
    {
        bool wasPlaying = reader.PlaybackDirection != 0;
        reader.PausePlayback();
        int wordNumber = CurrentWord;
        int offset = reader.TextAnchor - words[wordNumber].Index;
        List<int> starts = SyllableStarts(words[wordNumber].Value);
        int syllable = 0;
        while (syllable + 1 < starts.Count && starts[syllable + 1] <= offset)
            syllable++;

        int direction = Math.Sign(movement);
        bool moved = false;
        for (int count = Math.Abs(movement); count > 0; count--)
        {
            int next = syllable + direction;
            if (next >= 0 && next < starts.Count)
                syllable = next;
            else
            {
                int nextWord = wordNumber + direction;
                if (nextWord < 0 || nextWord >= words.Count)
                    break;
                wordNumber = nextWord;
                starts = SyllableStarts(words[wordNumber].Value);
                syllable = direction > 0 ? 0 : starts.Count - 1;
            }
            moved = true;
        }

        if (!moved && !wasPlaying)
            return;
        Match word = words[wordNumber];
        int end = syllable + 1 < starts.Count ? starts[syllable + 1] : word.Length;
        reader.SeekWord(word.Index, starts[syllable], end - starts[syllable]);
    }

    static List<int> SyllableStarts(string text)
    {
        var starts = new List<int> { 0 };
        int end = text.Length;
        while (end > 0 && !Char.IsLetterOrDigit(text[end - 1]))
            end--;

        int previousVowelEnd = -1;
        for (int index = 0; index < end;)
        {
            if (!IsVowel(text, index))
            {
                index++;
                continue;
            }
            int vowelStart = index++;
            while (index < end && IsVowel(text, index))
                index++;

            // An isolated final e is usually silent, except in endings such as table.
            bool finalE = vowelStart == end - 1 && Char.ToLowerInvariant(text[vowelStart]) == 'e';
            bool consonantLe = end >= 4 && Char.ToLowerInvariant(text[end - 2]) == 'l' &&
                Char.IsLetter(text[end - 3]) && !IsVowel(text, end - 3);
            if (previousVowelEnd >= 0 && (!finalE || consonantLe))
            {
                int start = vowelStart;
                while (start > previousVowelEnd && !Char.IsLetter(text[start - 1]))
                    start--;
                if (start > previousVowelEnd)
                    start--;
                if (consonantLe && finalE)
                    start = Math.Max(previousVowelEnd, end - 3);
                starts.Add(start);
            }
            previousVowelEnd = index;
        }
        return starts;
    }

    static bool IsVowel(string text, int index)
    {
        char value = Char.ToLowerInvariant(text[index]);
        return "aeiou".IndexOf(value) >= 0 ||
            (value == 'y' && index > 0 && Char.IsLetter(text[index - 1]));
    }

    void MoveByWords(int direction)
    {
        if (words == null || words.Count == 0 || reader.IsSizingEffects)
            return;
        bool wasPlaying = reader.PlaybackDirection != 0;
        reader.PausePlayback();
        int current = WordNumber(reader.TextAnchor);
        int next = Math.Max(0, Math.Min(words.Count - 1, current + direction));
        if (next != current || wasPlaying)
            reader.SeekWord(words[next].Index);
    }

    int WordNumber(int anchor)
    {
        int low = 0, high = words.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (words[middle].Index <= anchor)
                low = middle + 1;
            else
                high = middle;
        }
        return Math.Max(0, low - 1);
    }

    int SourceWord(int visibleOffset)
    {
        if (markdown == null || sourceWords.Count == 0)
            return 0;
        int offset = markdown.SourceOffset(visibleOffset);
        int low = 0, high = sourceWords.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (sourceWords[middle].Index <= offset)
                low = middle + 1;
            else
                high = middle;
        }
        return Math.Max(0, low - 1);
    }

    int DisplayWordOffset(int sourceWord)
    {
        if (words.Count == 0 || sourceWords.Count == 0)
            return 0;
        int offset = markdown.VisibleOffset(sourceWords[Math.Min(sourceWord, sourceWords.Count - 1)].Index);
        int visibleWord = WordNumber(offset);
        if (visibleWord + 1 < words.Count && offset >= words[visibleWord].Index + words[visibleWord].Length)
            visibleWord++;
        return words[visibleWord].Index;
    }

    void SetZoom(float value)
    {
        // WordCraft's zoom belongs to Flow, independently of the hidden RichEdit buffer.
        reader.ZoomOverride = FlowTuning.TextSize.ClampInput(Math.Round(value * 100)) / 100f;
        if (words != null)
        {
            int anchor = reader.TextAnchor;
            reader.ShowReader(source, anchor, false);
        }
        if (tuning != null && tuning.Visible)
            tuning.RefreshValues();
    }

    public bool HandleKey(Forms.Keys key)
    {
        switch (key)
        {
            case Forms.Keys.Control | Forms.Keys.Space:
                reader.PausePlayback();
                reader.StartPlayback(-1);
                return true;
            case Forms.Keys.Shift | Forms.Keys.Space:
            case Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.Space:
                reader.PausePlayback();
                if (words != null && words.Count > 0)
                {
                    reader.SeekWord(words[0].Index);
                    if ((key & Forms.Keys.Control) != 0)
                        reader.StartPlayback(1);
                }
                return true;
        }
        if (key == Forms.Keys.Left || key == Forms.Keys.Right)
        {
            MoveByWords(key == Forms.Keys.Left ? -1 : 1);
            return true;
        }
        if (key == Forms.Keys.Home || key == Forms.Keys.End)
            reader.ResetSyllableFocus();
        if (key == Forms.Keys.C || key == Forms.Keys.Space || key == Forms.Keys.Home || key == Forms.Keys.End)
            return reader.HandleKey(key);
        return false;
    }

    internal bool OwnsTuningWindow(IntPtr handle)
    {
        return tuning != null && tuning.IsHandleCreated && tuning.Visible && tuning.Handle == handle;
    }

    internal bool ScrollTuning(Forms.Message message)
    {
        return tuning != null && tuning.HandleVerticalWheel(message);
    }

    internal void ToggleTuning(IntPtr owner)
    {
        if (tuning == null || tuning.IsDisposed)
        {
            tuning = new FlowTuningPanel(reader, () => reader.Zoom, SetZoom, () => { }, SaveDefaults, RestoreDefaults, Forms.Keys.CapsLock);
            tuning.Text = "WordCraft Flow tuning  ·  Caps Lock to hide";
            tuning.HiddenByUser = () => reader.Focus();
            tuning.FullscreenRequested = () => Shortcut(Forms.Keys.Oemtilde, false);
        }
        if (tuning.Visible)
        {
            tuning.Hide();
            reader.Focus();
            return;
        }
        tuning.RefreshValues();
        var bounds = Forms.Screen.FromHandle(owner).WorkingArea;
        tuning.Height = Math.Min(740, bounds.Height - 40);
        tuning.Location = new System.Drawing.Point(bounds.Right - tuning.Width - 24, bounds.Top + 54);
        tuning.Show(new Owner(owner));
    }

    bool SaveDefaults()
    {
        var settings = new System.Collections.Generic.Dictionary<string, object>();
        foreach (var setting in FlowTuning.Settings)
            settings[setting.Key] = reader.Tuning.Get(setting.Key);
        settings["fontFamily"] = reader.FontFamilyName;
        settings["lettersPerSecond"] = reader.LettersPerSecond;
        settings["textZoomPercent"] = (int)Math.Round(reader.Zoom * 100);
        try
        {
            File.WriteAllText(defaultsPath, new JavaScriptSerializer().Serialize(settings));
            return true;
        }
        catch (IOException error) { Forms.MessageBox.Show(error.Message, "WordCraft"); }
        catch (UnauthorizedAccessException error) { Forms.MessageBox.Show(error.Message, "WordCraft"); }
        return false;
    }

    bool RestoreDefaults()
    {
        System.Collections.Generic.Dictionary<string, object> settings = null;
        try
        {
            if (File.Exists(defaultsPath))
                settings = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                    File.ReadAllText(defaultsPath));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        reader.ResetFontFamily();
        object font;
        if (settings != null && settings.TryGetValue("fontFamily", out font) && font is string)
            reader.SetFontFamily((string)font);
        foreach (var setting in FlowTuning.Settings)
            reader.SetTuning(setting.Key, ReadNumber(settings, setting.Key, setting.Default));
        reader.SetSpeed(ReadNumber(settings, "lettersPerSecond", 21));
        SetZoom(ReadNumber(settings, "textZoomPercent", 175) / 100f);
        return true;
    }

    static int ReadNumber(System.Collections.Generic.Dictionary<string, object> settings, string key, int fallback)
    {
        object value;
        return settings != null && settings.TryGetValue(key, out value) && value is int ? (int)value : fallback;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && tuning != null)
            tuning.Dispose();
        base.Dispose(disposing);
    }

    sealed class Owner : Forms.IWin32Window
    {
        public IntPtr Handle { get; private set; }
        public Owner(IntPtr handle) { Handle = handle; }
    }
}
