using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// One adjustable number in a view. Slot is its place in the shader's tuning buffer, or -1 when C# applies it.
// Minimum and Maximum are the slider's recommended range and Default is its recommended value. A typed number
// may go past the slider, as far as the hard limits, which keep the simulation from breaking.
sealed class TuningValue
{
    public readonly string Key, Label;
    public readonly double Minimum, Maximum, Default;
    public readonly int Slot;
    public double HardMinimum, HardMaximum;
    public double Value;

    public TuningValue(string key, string label, double minimum, double maximum, double value, int slot = -1)
    {
        Key = key;
        Label = label;
        Minimum = minimum;
        Maximum = maximum;
        Default = Value = value;
        Slot = slot;
        HardMinimum = minimum >= 0 ? 0 : minimum * 10;
        HardMaximum = maximum <= 0 ? 0 : maximum * 10;
        // Persistence values multiply each step, so anything above 1 grows without end.
        if (label.IndexOf("persistence", StringComparison.OrdinalIgnoreCase) >= 0)
            HardMaximum = 1;
    }

    public TuningValue Limit(double hardMinimum, double hardMaximum)
    {
        HardMinimum = hardMinimum;
        HardMaximum = hardMaximum;
        return this;
    }
}

// One color in a view. Slot is where its red, green and blue start in the shader's tuning buffer.
sealed class TuningColor
{
    public readonly string Key, Label;
    public readonly Color Default;
    public readonly int Slot;
    public Color Value;

    public TuningColor(string key, string label, byte red, byte green, byte blue, int slot = -1)
    {
        Key = key;
        Label = label;
        Default = Value = Color.FromRgb(red, green, blue);
        Slot = slot;
    }
}

// A setting picked from a list. Applied runs when the user picks, for choices that set other values.
sealed class TuningChoice
{
    public readonly string Key, Label;
    public readonly string[] Options;
    public readonly int Default;
    public int Value;
    public Action<int> Applied;

    public TuningChoice(string key, string label, string[] options, int value, Action<int> applied = null)
    {
        Key = key;
        Label = label;
        Options = options;
        Default = Value = value;
        Applied = applied;
    }
}

// Everything the tuning panel can change for one view.
sealed class ViewSettings
{
    public readonly string Prefix, DefaultFont;
    public readonly bool DefaultBold, DefaultConstant;
    public readonly TuningValue[] Values;
    public readonly TuningColor[] Colors;
    public TuningChoice[] Choices = new TuningChoice[0];
    public string Font;
    public bool Bold, Constant;

    public ViewSettings(string prefix, string font, bool bold, bool constant, TuningValue[] values, TuningColor[] colors)
    {
        Prefix = prefix;
        Font = DefaultFont = font;
        Bold = DefaultBold = bold;
        Constant = DefaultConstant = constant;
        Values = values;
        Colors = colors;
    }

    public ViewSettings With(params TuningChoice[] choices)
    {
        Choices = choices;
        return this;
    }
}

// Live values for every view. Defaults are the built-in look until the user saves their own.
static class ViewTuning
{
    public const int Slots = 128;
    static readonly string path = Path.Combine(
        Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")), "View-Tuning.txt");
    // In Tab order: Smoke, Water, Fluid, Particles, Ink.
    static readonly ViewSettings[] views =
    {
        new ViewSettings("smoke", "Segoe UI", true, false, new[]
        {
            new TuningValue("smoke.wordsize", "Word size", 0.2, 2.5, 1),
            new TuningValue("smoke.keep", "Motion persistence (drag)", 0.90, 0.999, 0.982, 0),
            new TuningValue("smoke.flowspeed", "Flow speed", 0.25, 3, 1, 6),
            new TuningValue("smoke.limit", "Speed limit", 2, 40, 20, 7),
            new TuningValue("smoke.iterations", "Pressure iterations (0 = quality tier)", 0, 60, 0).Limit(0, 400),
            new TuningValue("smoke.entrance", "Arrival kick", 0, 3, 1, 100),
            new TuningValue("smoke.amount", "Ink amount", 0.1, 2, 1, 101),
            new TuningValue("smoke.wake", "Letter wake ink", 0, 2, 1, 102),
            new TuningValue("smoke.strokes", "Pushes across a word", 1, 20, 6).Limit(1, 100),
            new TuningValue("smoke.drift", "Idle drift", 0, 0.15, 0.035, 1),
            new TuningValue("smoke.curl", "Curl", 0, 1, 0.20, 2),
            new TuningValue("smoke.spread", "Dye diffusion", 0, 0.10, 0.024, 3),
            new TuningValue("smoke.fade", "Dye persistence", 0.990, 1, 1, 4),
            new TuningValue("smoke.glow", "Ink opacity (brightness)", 0.3, 1, 0.88, 5),
            new TuningValue("smoke.grain", "Grain in new ink", 0, 2, 1, 110).Limit(0, 4),
            new TuningValue("smoke.shadelow", "Lightest ink shade (0 = background, 1 = ink color)", 0, 1, 0.11, 104),
            new TuningValue("smoke.shadehigh", "Strongest ink shade (0 = background, 1 = ink color)", 0, 1, 0.63, 105),
            new TuningValue("smoke.push", "Letter push", 0, 3, 1),
            new TuningValue("smoke.size", "Stroke size", 0.3, 3, 1),
            new TuningValue("smoke.sat", "Random and rainbow saturation", 0, 1, 0.55),
            new TuningValue("smoke.bright", "Random and rainbow brightness", 0.1, 1, 0.95),
            new TuningValue("smoke.huestep", "Rainbow step", 0.01, 0.5, 0.382),
            new TuningValue("smoke.dark", "Dark version brightness (random and rainbow)", 0, 1, 0.25)
        }, new[]
        {
            new TuningColor("smoke.ink", "Ink color (palette 1)", 255, 255, 255, 16),
            new TuningColor("smoke.c2", "Palette 2", 0x7F, 0xA7, 0xE8),
            new TuningColor("smoke.c3", "Palette 3", 0xE8, 0x9A, 0xB0),
            new TuningColor("smoke.c4", "Palette 4", 0x9E, 0xD9, 0xB8),
            new TuningColor("smoke.c5", "Palette 5", 0xF2, 0xD0, 0x8A),
            new TuningColor("smoke.d1", "Dark partner of palette 1", 0x3A, 0x42, 0x58),
            new TuningColor("smoke.d2", "Dark partner of palette 2", 0x1F, 0x35, 0x66),
            new TuningColor("smoke.d3", "Dark partner of palette 3", 0x6A, 0x23, 0x38),
            new TuningColor("smoke.d4", "Dark partner of palette 4", 0x24, 0x57, 0x40),
            new TuningColor("smoke.d5", "Dark partner of palette 5", 0x6B, 0x4F, 0x14),
            new TuningColor("smoke.paper", "Background color", 0x18, 0x1F, 0x2F, 20)
        }).With(new TuningChoice("smoke.colormode", "Color mode", new[] { "One color", "Palette in turn", "Palette at random", "Random colors", "Rainbow" }, 0),
            new TuningChoice("smoke.colorstep", "New color every", new[] { "Letter", "Word" }, 1),
            new TuningChoice("smoke.alternate", "Alternate keystrokes", new[] { "Off", "Light and dark of each color", "Two colors (palette 1, then palette 2)" }, 0)),
        new ViewSettings("water", "Times New Roman", false, true, new[]
        {
            new TuningValue("water.wordsize", "Word size", 0.2, 2.5, 1),
            new TuningValue("water.rain", "Rain (drops per second)", 0, 12, 0.22),
            new TuningValue("water.drops", "Raindrop ripples", 0, 3, 1),
            new TuningValue("water.dropsize", "Raindrop size", 0.2, 4, 1, 103).Limit(0.05, 20),
            new TuningValue("water.gain", "Typing ripples", 0, 30, 13, 8),
            new TuningValue("water.keep", "Ripple persistence (damping)", 0.90, 0.999, 0.982, 9),
            new TuningValue("water.speed", "Ripple speed", 0.05, 0.45, 0.30, 10).Limit(0.01, 0.49),
            new TuningValue("water.flutter", "Letter flutter", 0, 2, 1, 11),
            new TuningValue("water.swell", "Idle swell", 0, 2, 1, 12),
            new TuningValue("water.bend", "Refraction", 0, 25, 11, 13),
            new TuningValue("water.caustics", "Caustic light", 0, 0.5, 0.13, 14),
            new TuningValue("water.causticthreshold", "Caustic threshold (higher shows only the brightest lines)", 0.3, 3, 1, 120).Limit(0.05, 20),
            new TuningValue("water.causticsharp", "Caustic sharpness", 0.2, 5, 1, 121).Limit(0.05, 50),
            new TuningValue("water.causticsoft", "Caustic softness", 0, 4, 1, 122),
            new TuningValue("water.depth", "Water depth (caustic size and bend)", 0.05, 0.8, 0.29, 123).Limit(0.01, 3),
            new TuningValue("water.lightaround", "Sun direction (degrees around)", -180, 180, 35.3, 124).Limit(-3600, 3600),
            new TuningValue("water.lighttilt", "Sun tilt (degrees from overhead)", 0, 60, 16.4, 125).Limit(0, 80),
            new TuningValue("water.specks", "Sparkle specks", 0, 1, 0.23, 15),
            new TuningValue("water.specksglow", "Speck glow", 0, 2, 0, 106),
            new TuningValue("water.specksfine", "Speck fineness", 0.25, 3, 1, 107).Limit(0.05, 20),
            new TuningValue("water.twinkle", "Speck twinkle", 0, 1, 0, 108),
            new TuningValue("water.angle", "Deep to shallow direction (degrees)", -180, 180, 13, 109).Limit(-3600, 3600),
            new TuningValue("water.space", "Space wave", 0, 3, 1)
        }, new[]
        {
            new TuningColor("water.deep", "Deep water color", 24, 113, 140, 24),
            new TuningColor("water.shallow", "Shallow water color", 125, 196, 179, 28),
            new TuningColor("water.caustic", "Caustic light color", 156, 224, 212, 84),
            new TuningColor("water.highlight", "Sparkle highlight color", 94, 199, 235, 88),
            new TuningColor("water.speckcolor", "Speck color", 145, 204, 189, 112)
        }),
        new ViewSettings("fluid", "Segoe UI", true, false, new[]
        {
            new TuningValue("fluid.wordsize", "Word size", 0.2, 2.5, 1),
            new TuningValue("fluid.ink", "Word ink", 0.05, 1.5, 0.45),
            new TuningValue("fluid.curl", "Curl", 0, 1.5, 0.35),
            new TuningValue("fluid.keep", "Motion persistence (drag)", 0.90, 0.999, 0.995),
            new TuningValue("fluid.flowspeed", "Flow speed", 0.25, 3, 1),
            new TuningValue("fluid.limit", "Speed limit", 2, 60, 30),
            new TuningValue("fluid.iterations", "Pressure iterations (0 = quality tier)", 0, 60, 0).Limit(0, 400),
            new TuningValue("fluid.reach", "Push reach (flow cells)", 1, 12, 3).Limit(1, 40),
            new TuningValue("fluid.fade", "Dye persistence", 0.95, 1, 0.997),
            new TuningValue("fluid.exposure", "Brightness", 0.3, 4, 1.4),
            new TuningValue("fluid.glow", "Glow", 0, 2, 0.6),
            new TuningValue("fluid.push", "Word push", 0, 3, 1),
            new TuningValue("fluid.newest", "Newest letter push", 0, 3, 0.3),
            new TuningValue("fluid.sat", "Random and rainbow saturation", 0, 1, 1),
            new TuningValue("fluid.bright", "Random and rainbow brightness", 0.1, 1, 1),
            new TuningValue("fluid.huestep", "Rainbow step", 0.01, 0.5, 0.382),
            new TuningValue("fluid.dark", "Dark version brightness (random and rainbow)", 0, 1, 0.15)
        }, new[]
        {
            new TuningColor("fluid.background", "Background color", 8, 11, 22, 116),
            new TuningColor("fluid.color", "Dye color (palette 1)", 255, 0, 200),
            new TuningColor("fluid.c2", "Palette 2", 0x00, 0xD4, 0xFF),
            new TuningColor("fluid.c3", "Palette 3", 0xFF, 0xD0, 0x00),
            new TuningColor("fluid.c4", "Palette 4", 0x3C, 0xFF, 0x7A),
            new TuningColor("fluid.c5", "Palette 5", 0x8A, 0x3C, 0xFF),
            new TuningColor("fluid.d1", "Dark partner of palette 1", 0x3A, 0x00, 0x2D),
            new TuningColor("fluid.d2", "Dark partner of palette 2", 0x00, 0x30, 0x3A),
            new TuningColor("fluid.d3", "Dark partner of palette 3", 0x3A, 0x2F, 0x00),
            new TuningColor("fluid.d4", "Dark partner of palette 4", 0x0D, 0x3A, 0x1C),
            new TuningColor("fluid.d5", "Dark partner of palette 5", 0x1F, 0x0D, 0x3A)
        }).With(new TuningChoice("fluid.colormode", "Color mode", new[] { "One color", "Palette in turn", "Palette at random", "Random colors", "Rainbow" }, 4),
            new TuningChoice("fluid.colorstep", "New color every", new[] { "Letter", "Word" }, 0),
            new TuningChoice("fluid.alternate", "Alternate keystrokes", new[] { "Off", "Light and dark of each color", "Two colors (palette 1, then palette 2)" }, 1)),
        new ViewSettings("particles", "Segoe UI", true, false, new[]
        {
            new TuningValue("particles.wordsize", "Word size", 0.2, 2.5, 1),
            new TuningValue("particles.count", "Particle count (thousands, 0 = quality tier)", 0, 1000, 0).Limit(0, 4000),
            new TuningValue("particles.spawn", "New particles per letter (share of swarm)", 0, 0.25, 0.05).Limit(0, 1),
            new TuningValue("particles.outline", "Outline thickness (pixels)", 0.5, 12, 3).Limit(0.25, 60),
            new TuningValue("particles.burst", "Word burst particles per letter (share of swarm)", 0, 0.25, 0.06).Limit(0, 1),
            new TuningValue("particles.void", "Burst push (throws the whole word out)", 0, 4, 1),
            new TuningValue("particles.gridpull", "Grid magnetics strength", 0, 0.03, 0.003).Limit(0, 0.5),
            new TuningValue("particles.glow", "Glow brightness", 0, 6, 2.5),
            new TuningValue("particles.glowkeep", "Glow persistence", 0.9, 0.999, 0.985),
            new TuningValue("particles.follow", "Follow the flow", 0.01, 1, 0.12),
            new TuningValue("particles.size", "Particle size", 0.5, 4, 1.4),
            new TuningValue("particles.light", "Particle light", 0.05, 2, 0.5),
            new TuningValue("particles.speed", "Speed color", 0, 3, 1),
            new TuningValue("particles.exposure", "Brightness", 0.3, 4, 1.2),
            new TuningValue("particles.curl", "Curl", 0, 1.5, 0.5),
            new TuningValue("particles.keep", "Motion persistence (drag)", 0.90, 0.999, 0.995),
            new TuningValue("particles.flowspeed", "Flow speed", 0.25, 3, 1),
            new TuningValue("particles.limit", "Speed limit", 2, 60, 30),
            new TuningValue("particles.iterations", "Pressure iterations (0 = quality tier)", 0, 60, 0).Limit(0, 400),
            new TuningValue("particles.reach", "Push reach (flow cells)", 1, 12, 3).Limit(1, 40),
            new TuningValue("particles.push", "Word push", 0, 3, 1),
            new TuningValue("particles.sat", "Random and rainbow saturation", 0, 1, 0.7),
            new TuningValue("particles.bright", "Random and rainbow brightness", 0.1, 1, 1),
            new TuningValue("particles.huestep", "Rainbow step", 0.01, 0.5, 0.382)
        }, new[]
        {
            new TuningColor("particles.slow", "Slow particle color", 10, 77, 242, 44),
            new TuningColor("particles.fast", "Fast particle color", 38, 230, 255, 48),
            new TuningColor("particles.background", "Background color", 2, 3, 7, 96),
            new TuningColor("particles.glowcolor", "Glow color (palette 1)", 179, 255, 240),
            new TuningColor("particles.c2", "Glow palette 2", 0x7A, 0xC8, 0xFF),
            new TuningColor("particles.c3", "Glow palette 3", 0xD9, 0x9C, 0xFF),
            new TuningColor("particles.c4", "Glow palette 4", 0x8C, 0xFF, 0xB0),
            new TuningColor("particles.c5", "Glow palette 5", 0xFF, 0xE3, 0x9A)
        }).With(new TuningChoice("particles.colormode", "Color mode", new[] { "One color", "Palette in turn", "Palette at random", "Random colors", "Rainbow" }, 0),
            new TuningChoice("particles.colorstep", "New color every", new[] { "Letter", "Word" }, 0),
            new TuningChoice("particles.grid", "Grid magnetics (particles drift back into an even grid)", new[] { "Off", "On" }, 1)),
        // Ink runs Smoke's system. Its own motion values replace Smoke's in the shader.
        new ViewSettings("ink", "Segoe UI", true, false, new[]
        {
            new TuningValue("ink.wordsize", "Word size", 0.2, 2.5, 1),
            new TuningValue("ink.strength", "Ink strength", 0.05, 2, 0.6),
            new TuningValue("ink.fade", "Ink persistence", 0.97, 1, 0.996),
            new TuningValue("ink.keep", "Motion persistence (drag)", 0.90, 0.999, 0.985),
            new TuningValue("ink.flowspeed", "Flow speed", 0.25, 3, 1, -1),
            new TuningValue("ink.limit", "Speed limit", 2, 40, 20, -1),
            new TuningValue("ink.iterations", "Pressure iterations (0 = quality tier)", 0, 60, 0).Limit(0, 400),
            new TuningValue("ink.entrance", "Arrival kick", 0, 3, 1, -1),
            new TuningValue("ink.wake", "Letter wake ink", 0, 2, 1, -1),
            new TuningValue("ink.strokes", "Pushes across a word", 1, 20, 6).Limit(1, 100),
            new TuningValue("ink.drift", "Idle drift", 0, 0.15, 0.03),
            new TuningValue("ink.curl", "Curl", 0, 1, 0.35),
            new TuningValue("ink.spread", "Ink diffusion", 0, 0.10, 0.01),
            new TuningValue("ink.grain", "Grain in new ink", 0, 2, 1).Limit(0, 4),
            new TuningValue("ink.push", "Letter push", 0, 3, 1),
            new TuningValue("ink.size", "Stroke size", 0.3, 3, 1)
        }, new[]
        {
            new TuningColor("ink.paper", "Paper color", 0xF2, 0xEC, 0xDF, 52),
            new TuningColor("ink.color1", "Ink 1", 0x1C, 0x1C, 0x22, 56),
            new TuningColor("ink.color2", "Ink 2", 0x1C, 0x4B, 0x7D, 60),
            new TuningColor("ink.color3", "Ink 3", 0xC2, 0x3B, 0x2E, 64),
            new TuningColor("ink.color4", "Ink 4", 0x3A, 0x7D, 0x5D, 68),
            new TuningColor("ink.color5", "Ink 5", 0xC9, 0x94, 0x1A, 72)
        }).With(new TuningChoice("ink.colormode", "Ink order",
            new[] { "Next ink each word", "Random ink each word", "Next ink each letter", "Ink 1 only" }, 0))
    };
    static readonly Dictionary<string, TuningValue> byKey = new Dictionary<string, TuningValue>();
    static readonly Dictionary<string, TuningColor> colorByKey = new Dictionary<string, TuningColor>();
    static readonly Dictionary<string, TuningChoice> choiceByKey = new Dictionary<string, TuningChoice>();
    // Colors picked while typing, such as Smoke's ink for this word. They override the saved color in that slot.
    static readonly Dictionary<int, Color> currents = new Dictionary<int, Color>();
    static readonly Dictionary<string, Typeface> faces = new Dictionary<string, Typeface>();
    static readonly float[] shader = new float[Slots];
    static readonly float[] paperShader = new float[Slots];
    static readonly float[] smokeOverrideShader = new float[Slots];
    const int SmokeTurbulenceSlot = 111, CloudLightingSlot = 126, HeldTransportSlot = 127;
    // Shader slots of Smoke's motion values, and the Ink values that take their place in the Ink view.
    static readonly int[] inkSlots = { 0, 1, 2, 3, 4, 5, 6, 7, 100, 102, 110 };
    static readonly string[] inkMotion = { "ink.keep", "ink.drift", "ink.curl", "ink.spread", "ink.fade", "ink.strength",
        "ink.flowspeed", "ink.limit", "ink.entrance", "ink.wake", "ink.grain" };

    // Rises when a font, word size or sizing style changes, so the word display reshapes the word.
    public static int Revision { get; private set; }

    static ViewTuning()
    {
        foreach (ViewSettings view in views)
        {
            foreach (TuningValue value in view.Values)
                byKey[value.Key] = value;
            foreach (TuningColor color in view.Colors)
                colorByKey[color.Key] = color;
            foreach (TuningChoice choice in view.Choices)
                choiceByKey[choice.Key] = choice;
        }
        try
        {
            if (!File.Exists(path))
                return;
            foreach (string line in File.ReadAllLines(path))
            {
                int split = line.IndexOf('=');
                if (split <= 0)
                    continue;
                string key = line.Substring(0, split).Trim(), text = line.Substring(split + 1).Trim();
                TuningValue value;
                TuningColor color;
                TuningChoice choice;
                double saved;
                int picked;
                if (choiceByKey.TryGetValue(key, out choice))
                {
                    if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out picked) &&
                        picked >= 0 && picked < choice.Options.Length)
                        choice.Value = picked;
                }
                else if (byKey.TryGetValue(key, out value))
                {
                    if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out saved))
                        value.Value = Math.Max(value.HardMinimum, Math.Min(value.HardMaximum, saved));
                }
                else if (colorByKey.TryGetValue(key, out color))
                {
                    int rgb;
                    if (text.Length == 7 && text[0] == '#' && Int32.TryParse(text.Substring(1), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out rgb))
                        color.Value = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                }
                else
                    foreach (ViewSettings view in views)
                    {
                        if (key == view.Prefix + ".font" && text.Length > 0) view.Font = text;
                        else if (key == view.Prefix + ".bold") view.Bold = text == "1";
                        else if (key == view.Prefix + ".constant") view.Constant = text == "1";
                    }
            }
        }
        catch (IOException error) { Trace.WriteLine(error.Message); }
        catch (UnauthorizedAccessException error) { Trace.WriteLine(error.Message); }
    }

    public static ViewSettings For(WordViewMode mode) { return views[(int)mode]; }
    public static string Prefix(WordViewMode mode) { return views[(int)mode].Prefix; }
    public static float Get(string key) { return (float)byKey[key].Value; }
    public static Color ColorOf(string key) { return colorByKey[key].Value; }
    internal static void SetColor(string key, Color color) { colorByKey[key].Value=color; Touch(); }
    public static int Choice(string key) { return choiceByKey[key].Value; }

    // Sets the color a shader slot shows for the current letter or word.
    public static void SetCurrent(int slot, Color color) { currents[slot] = color; }

    public static Brush BrushOf(string key) { return new SolidColorBrush(colorByKey[key].Value); }
    public static void Touch() { ++Revision; }

    public static bool Flag(WordViewMode mode, string name)
    {
        return name == "constant" ? views[(int)mode].Constant : views[(int)mode].Bold;
    }

    public static Typeface Face(WordViewMode mode)
    {
        ViewSettings view = views[(int)mode];
        string name = view.Font + (view.Bold ? "|bold" : "|regular");
        Typeface face;
        if (!faces.TryGetValue(name, out face))
            faces[name] = face = new Typeface(new FontFamily(view.Font + ", Segoe UI, Global User Interface"),
                FontStyles.Normal, view.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return face;
    }

    // The values the shaders read, in buffer order.
    public static float[] Shader()
    {
        foreach (TuningValue value in byKey.Values)
            if (value.Slot >= 0)
                shader[value.Slot] = (float)value.Value;
        foreach (TuningColor color in colorByKey.Values)
            if (color.Slot >= 0)
            {
                shader[color.Slot] = color.Value.R / 255f;
                shader[color.Slot + 1] = color.Value.G / 255f;
                shader[color.Slot + 2] = color.Value.B / 255f;
            }
        foreach (KeyValuePair<int, Color> current in currents)
        {
            shader[current.Key] = current.Value.R / 255f;
            shader[current.Key + 1] = current.Value.G / 255f;
            shader[current.Key + 2] = current.Value.B / 255f;
        }
        return shader;
    }

    // The Ink view reads Smoke's motion slots, filled with its own values.
    public static float[] Shader(bool paper, bool holdSmokeDye = false, bool turbulentSmoke = false,
        float cloudLighting = 0, bool coastSmoke = false, float readingMotion = 0, float billowDetail = 0, float coastMotion = -1, bool flowImpulses = false, bool typingCoast = false)
    {
        float[] values = Shader();
        if (!paper)
        {
            if (typingCoast)
            {
                // Keep Typing's pigment transport and rendering. Only suspend
                // fade/diffusion and let its existing momentum run a little faster.
                Array.Copy(values, smokeOverrideShader, Slots);
                if (coastSmoke)
                {
                    smokeOverrideShader[byKey["smoke.fade"].Slot] = 1;
                    smokeOverrideShader[byKey["smoke.spread"].Slot] = 0;
                    smokeOverrideShader[byKey["smoke.keep"].Slot] = 1;
                    smokeOverrideShader[byKey["smoke.flowspeed"].Slot] *= 1.65f;
                }
                return smokeOverrideShader;
            }
            if (!holdSmokeDye && !turbulentSmoke && cloudLighting == 0 && !coastSmoke && readingMotion == 0 && billowDetail == 0 && coastMotion <= 0 && !flowImpulses)
                return values;
            Array.Copy(values, smokeOverrideShader, Slots);
            if (holdSmokeDye || coastSmoke)
            {
                smokeOverrideShader[byKey["smoke.fade"].Slot] = 1;
                smokeOverrideShader[byKey["smoke.spread"].Slot] = 0;
            }
            if (coastSmoke)
            {
                smokeOverrideShader[byKey["smoke.keep"].Slot] = 1;
                smokeOverrideShader[byKey["smoke.drift"].Slot] = 0;
                smokeOverrideShader[byKey["smoke.flowspeed"].Slot] *= 3.2f;
            }
            // The same broad curls carry through the handoff. Reading leaves room for letter-driven wakes.
            smokeOverrideShader[SmokeTurbulenceSlot] = turbulentSmoke ? 1 : coastMotion >= 0 ? coastMotion : coastSmoke ? .86f : 0;
            if (holdSmokeDye && !flowImpulses)
                smokeOverrideShader[byKey["smoke.curl"].Slot] = Math.Min(.28f, smokeOverrideShader[byKey["smoke.curl"].Slot]);
            smokeOverrideShader[CloudLightingSlot] = cloudLighting;
            // The unused fourth component of the Smoke paper color carries its surface relief.
            smokeOverrideShader[19] = flowImpulses ? 1 : 0;
            smokeOverrideShader[23] = billowDetail;
            smokeOverrideShader[HeldTransportSlot] = holdSmokeDye || coastSmoke ? 1 : 0;
            return smokeOverrideShader;
        }
        Array.Copy(values, paperShader, Slots);
        for (int i = 0; i < inkMotion.Length; ++i)
            paperShader[inkSlots[i]] = Get(inkMotion[i]);
        return paperShader;
    }

    public static void Reset(WordViewMode mode)
    {
        ViewSettings view = views[(int)mode];
        foreach (TuningValue value in view.Values)
            value.Value = value.Default;
        foreach (TuningColor color in view.Colors)
            color.Value = color.Default;
        foreach (TuningChoice choice in view.Choices)
            choice.Value = choice.Default;
        view.Font = view.DefaultFont;
        view.Bold = view.DefaultBold;
        view.Constant = view.DefaultConstant;
        ++Revision;
    }

    // Only values the user changed are written, so untouched values follow the built-in defaults.
    public static bool Save()
    {
        var lines = new List<string>();
        foreach (ViewSettings view in views)
        {
            if (view.Font != view.DefaultFont)
                lines.Add(view.Prefix + ".font=" + view.Font);
            if (view.Bold != view.DefaultBold)
                lines.Add(view.Prefix + ".bold=" + (view.Bold ? "1" : "0"));
            if (view.Constant != view.DefaultConstant)
                lines.Add(view.Prefix + ".constant=" + (view.Constant ? "1" : "0"));
            foreach (TuningValue value in view.Values)
                if (Math.Abs(value.Value - value.Default) > 1e-9)
                    lines.Add(value.Key + "=" + value.Value.ToString("R", CultureInfo.InvariantCulture));
            foreach (TuningColor color in view.Colors)
                if (color.Value != color.Default)
                    lines.Add(String.Format("{0}=#{1:X2}{2:X2}{3:X2}", color.Key, color.Value.R, color.Value.G, color.Value.B));
            foreach (TuningChoice choice in view.Choices)
                if (choice.Value != choice.Default)
                    lines.Add(choice.Key + "=" + choice.Value.ToString(CultureInfo.InvariantCulture));
        }
        try
        {
            File.WriteAllLines(path, lines);
            return true;
        }
        catch (IOException error) { Trace.WriteLine(error.Message); }
        catch (UnauthorizedAccessException error) { Trace.WriteLine(error.Message); }
        return false;
    }
}

// Caps Lock shows this over the view. Nothing in it takes keyboard focus, so typing continues while tuning.
sealed class TuningPanel : Border
{
    static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xC8, 0xD4, 0xEA));
    static readonly Brush Quiet = new SolidColorBrush(Color.FromRgb(0x7C, 0x8D, 0xAE));
    static readonly string[] fontNames = Fonts.SystemFontFamilies.Select(family => family.Source)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    readonly StackPanel rows = new StackPanel();
    readonly TextBlock title = new TextBlock { Foreground = Ink, FontSize = 15, FontWeight = FontWeights.SemiBold };
    readonly TextBlock status = new TextBlock { Foreground = Quiet, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    readonly ScrollViewer scroll;
    readonly WordViewEffects effects;
    WordViewMode mode;
    // Called after every use of the panel, so typing goes back to the word.
    public Action Used;

    public TuningPanel(WordViewEffects effects)
    {
        this.effects = effects;
        Width = 300;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 48, 16, 16);
        Padding = new Thickness(14);
        CornerRadius = new CornerRadius(8);
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x14, 0x1E));
        Visibility = Visibility.Collapsed;
        Focusable = false;
        PreviewMouseUp += (sender, args) => Dispatcher.BeginInvoke(new Action(Done),
            System.Windows.Threading.DispatcherPriority.Input);

        var save = new Button { Content = "Save defaults", Focusable = false, Padding = new Thickness(10, 4, 10, 4) };
        save.Click += (sender, args) =>
            status.Text = ViewTuning.Save() ? "Saved. Every view starts from these values." : "Could not write the defaults file.";
        var reset = new Button
        {
            Content = "Reset", Focusable = false, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0)
        };
        reset.Click += (sender, args) =>
        {
            ViewTuning.Reset(mode);
            Show(mode);
            effects.Wake();
            status.Text = "Built-in values. Save defaults to keep them.";
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(reset);

        // The rows scroll with the bar when the window is short. The mouse wheel still moves between words.
        scroll = new ScrollViewer
        {
            Content = rows, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0)
        };
        SizeChanged += (sender, args) => FitRows();
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(scroll);
        stack.Children.Add(buttons);
        stack.Children.Add(status);
        Child = stack;
    }

    void FitRows()
    {
        var host = Parent as FrameworkElement;
        if (host != null && host.ActualHeight > 0)
            scroll.MaxHeight = Math.Max(120, host.ActualHeight - 210);
    }

    public void Show(WordViewMode view)
    {
        mode = view;
        ViewSettings settings = ViewTuning.For(view);
        title.Text = view == WordViewMode.SumiInk ? "Smoke" : view.ToString();
        status.Text = String.Empty;
        rows.Children.Clear();
        AddRow("Quality", 0, 3, (int)GpuQuality.Tier, true,
            value => ((QualityTier)(int)value).ToString(),
            value => effects.SetQuality((QualityTier)(int)value));

        AddCaption("Font");
        var fonts = new ComboBox { ItemsSource = fontNames, SelectedItem = settings.Font, Focusable = false, MaxDropDownHeight = 320 };
        // Each name is shown in its own font. Only the visible names are drawn.
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        name.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding());
        name.SetValue(TextBlock.FontSizeProperty, 15.0);
        fonts.ItemTemplate = new DataTemplate { VisualTree = name };
        VirtualizingPanel.SetIsVirtualizing(fonts, true);
        fonts.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        fonts.SelectionChanged += (sender, args) =>
        {
            if (fonts.SelectedItem == null)
                return;
            settings.Font = (string)fonts.SelectedItem;
            Reshape();
        };
        // The font list opens a popup that can take the keyboard with it.
        fonts.IsTextSearchEnabled = false;
        fonts.DropDownClosed += (sender, args) => ReturnFocusSoon();
        rows.Children.Add(fonts);
        AddCheck("Bold", settings.Bold, value => settings.Bold = value);
        AddCheck("Constant word size", settings.Constant, value => settings.Constant = value);

        foreach (TuningValue item in settings.Values)
        {
            TuningValue value = item;
            bool reshapes = value.Key.EndsWith(".wordsize", StringComparison.Ordinal);
            AddRow(value.Label, value.Minimum, value.Maximum, value.Value, false,
                number => number.ToString(value.Maximum - value.Minimum < 0.02 ? "0.0000" :
                    value.Maximum - value.Minimum >= 20 ? "0.#" : "0.000", CultureInfo.InvariantCulture),
                number =>
                {
                    value.Value = number;
                    if (reshapes)
                        ViewTuning.Touch();
                    effects.Wake();
                },
                value.Default, value.HardMinimum, value.HardMaximum);
        }
        foreach (TuningChoice item in settings.Choices)
            AddChoice(item);
        foreach (TuningColor item in settings.Colors)
            AddColor(item);
        FitRows();
        Visibility = Visibility.Visible;
    }

    // After a click, the keyboard goes back to the word unless a number box was clicked.
    void Done()
    {
        if (!Editing)
            ReturnFocus();
    }

    // A closing drop-down list hands the keyboard back to itself after its event, so wait until it has settled.
    void ReturnFocusSoon()
    {
        Dispatcher.BeginInvoke(new Action(ReturnFocus), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    void ReturnFocus()
    {
        if (Used != null)
            Used();
    }

    // True while a number box in the panel is being typed in.
    public bool Editing { get { return IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox; } }

    // For the self-test: flip a switch the same way a click does.
    internal bool Toggle(string label)
    {
        foreach (UIElement row in rows.Children)
        {
            var check = row as CheckBox;
            if (check != null && (string)check.Content == label)
            {
                check.IsChecked = check.IsChecked != true;
                return true;
            }
        }
        return false;
    }

    void Reshape()
    {
        status.Text = String.Empty;
        ViewTuning.Touch();
        effects.Wake();
    }

    void AddCaption(string text)
    {
        rows.Children.Add(new TextBlock { Text = text, Foreground = Ink, FontSize = 12, Margin = new Thickness(0, 10, 0, 2) });
    }

    void AddCheck(string label, bool value, Action<bool> changed)
    {
        var check = new CheckBox
        {
            Content = label, IsChecked = value, Foreground = Ink, FontSize = 12, Focusable = false,
            Margin = new Thickness(0, 8, 0, 0)
        };
        RoutedEventHandler toggled = (sender, args) =>
        {
            changed(check.IsChecked == true);
            Reshape();
        };
        check.Checked += toggled;
        check.Unchecked += toggled;
        rows.Children.Add(check);
    }

    // A drop-down list. A choice that sets other values redraws the panel to show them.
    void AddChoice(TuningChoice choice)
    {
        AddCaption(choice.Label);
        var list = new ComboBox { ItemsSource = choice.Options, SelectedIndex = choice.Value, Focusable = false };
        list.SelectionChanged += (sender, args) =>
        {
            if (list.SelectedIndex < 0)
                return;
            choice.Value = list.SelectedIndex;
            status.Text = String.Empty;
            if (choice.Applied != null)
            {
                choice.Applied(choice.Value);
                Dispatcher.BeginInvoke(new Action(() => Show(mode)));
            }
            effects.Wake();
        };
        list.IsTextSearchEnabled = false;
        list.DropDownClosed += (sender, args) => ReturnFocusSoon();
        rows.Children.Add(list);
    }

    // A swatch that opens the Windows color picker.
    void AddColor(TuningColor color)
    {
        AddCaption(color.Label);
        var swatch = new Border
        {
            Height = 20, CornerRadius = new CornerRadius(3), BorderBrush = Quiet, BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(color.Value), Cursor = Cursors.Hand
        };
        swatch.MouseLeftButtonUp += (sender, args) =>
        {
            using (var picker = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true, Color = System.Drawing.Color.FromArgb(color.Value.R, color.Value.G, color.Value.B)
            })
            {
                if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;
                color.Value = Color.FromRgb(picker.Color.R, picker.Color.G, picker.Color.B);
            }
            swatch.Background = new SolidColorBrush(color.Value);
            status.Text = String.Empty;
            effects.Wake();
            Done();
        };
        rows.Children.Add(swatch);
    }

    // A slider over the recommended range, with a tick at the recommended value, and a box for any number.
    // Click the box, type a number and press Enter. Esc puts the value back. A typed number may go past the
    // slider, as far as the hard limits. The slider then rests at its end and the box shows the real value.
    void AddRow(string label, double minimum, double maximum, double value, bool steps,
        Func<double, string> describe, Action<double> changed,
        double recommended = Double.NaN, double hardMinimum = Double.NaN, double hardMaximum = Double.NaN)
    {
        if (Double.IsNaN(hardMinimum)) hardMinimum = minimum;
        if (Double.IsNaN(hardMaximum)) hardMaximum = maximum;
        double current = value;
        bool placing = false;
        var caption = new TextBlock
        {
            Text = label, Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 180
        };
        var number = new TextBox
        {
            Text = describe(value), Width = 76, FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x2D)), Foreground = Ink,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x3D, 0x52)), CaretBrush = Ink
        };
        var header = new DockPanel { Margin = new Thickness(0, 10, 0, 2), LastChildFill = false };
        DockPanel.SetDock(caption, Dock.Left);
        DockPanel.SetDock(number, Dock.Right);
        header.Children.Add(caption);
        header.Children.Add(number);
        var slider = new Slider
        {
            Minimum = minimum, Maximum = maximum, Value = Math.Max(minimum, Math.Min(maximum, value)),
            Focusable = false, IsMoveToPointEnabled = true, IsSnapToTickEnabled = steps,
            TickFrequency = steps ? 1 : (maximum - minimum) / 100
        };
        if (!steps && !Double.IsNaN(recommended))
        {
            // The tick marks the recommended value.
            slider.Ticks = new DoubleCollection { recommended };
            slider.TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight;
            slider.ToolTip = "Recommended: " + describe(recommended);
        }
        slider.ValueChanged += (sender, args) =>
        {
            if (placing)
                return;
            current = args.NewValue;
            if (!number.IsKeyboardFocused)
                number.Text = describe(current);
            status.Text = String.Empty;
            changed(current);
        };
        Action apply = () =>
        {
            double typed;
            string text = number.Text.Trim();
            bool parsed = Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out typed) ||
                Double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out typed);
            if (!parsed && steps)
            {
                // The Quality box also takes a tier name.
                QualityTier tier;
                if (Enum.TryParse(text, true, out tier))
                {
                    typed = (int)tier;
                    parsed = true;
                }
            }
            if (parsed && !Double.IsNaN(typed) && !Double.IsInfinity(typed))
            {
                typed = Math.Max(hardMinimum, Math.Min(hardMaximum, steps ? Math.Round(typed) : typed));
                if (typed != current)
                {
                    current = typed;
                    placing = true;
                    slider.Value = Math.Max(minimum, Math.Min(maximum, typed));
                    placing = false;
                    status.Text = String.Empty;
                    changed(current);
                }
            }
            number.Text = describe(current);
        };
        number.KeyDown += (sender, args) =>
        {
            if (args.Key == Key.Enter)
                apply();
            else if (args.Key == Key.Escape)
                number.Text = describe(current);
            else
                return;
            args.Handled = true;
            ReturnFocus();
        };
        number.LostKeyboardFocus += (sender, args) => apply();
        rows.Children.Add(header);
        rows.Children.Add(slider);
    }
}

// Writes unexpected errors to Bleeding-Edge/WordCraft-Errors.log and keeps WordCraft running when it can.
static class ErrorLog
{
    static readonly string path = Path.Combine(
        Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")), "WordCraft-Errors.log");

    public static void Watch(Application application)
    {
        application.DispatcherUnhandledException += (sender, args) =>
        {
            Write(args.Exception);
            // Keep the draft alive. The log tells the next session what went wrong.
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (sender, args) => Write(args.ExceptionObject as Exception);
    }

    public static void Write(Exception error)
    {
        try
        {
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " +
                typeof(ErrorLog).Assembly.GetName().Version + Environment.NewLine + error + Environment.NewLine +
                Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// Picks the color for each letter or word from a view's color mode, palette and random settings.
sealed class ColorScheme
{
    readonly string prefix;
    readonly string[] palette, darks;
    readonly Random random = new Random();
    int turn, picked = -1, keystrokes;
    double hue = 0.83;
    bool wordDone = true;
    Color current;

    // palette lists the view's five palette color keys, first to last. darks, when given, are their dark partners.
    public ColorScheme(string prefix, string[] palette, string[] darks = null)
    {
        this.prefix = prefix;
        this.palette = palette;
        this.darks = darks;
    }

    // The color for one keystroke, following the view's Alternate keystrokes choice. dark is true on the
    // keystrokes that should lay over what is there rather than add to it.
    public Color Keystroke(out bool dark)
    {
        int alternate = darks == null ? 0 : ViewTuning.Choice(prefix + ".alternate");
        bool second = keystrokes++ % 2 == 1;
        dark = alternate != 0 && second;
        if (alternate == 2)
            return ViewTuning.ColorOf(palette[second ? 1 : 0]);
        if (!dark)
            return Next();
        // The dark partner of the color just used, or a dimmed copy for random and rainbow colors.
        if (picked >= 0)
            return ViewTuning.ColorOf(darks[picked]);
        double level = Math.Max(0, Math.Min(1, ViewTuning.Get(prefix + ".dark")));
        return Color.FromRgb((byte)(current.R * level), (byte)(current.G * level), (byte)(current.B * level));
    }

    public Color Current { get { return current; } }

    // The color for this deposit. With New color every Word it stays the same until EndWord.
    public Color Next()
    {
        bool perWord = ViewTuning.Choice(prefix + ".colorstep") == 1;
        if (perWord && !wordDone)
            return current;
        wordDone = false;
        float saturation = ViewTuning.Get(prefix + ".sat"), brightness = ViewTuning.Get(prefix + ".bright");
        switch (ViewTuning.Choice(prefix + ".colormode"))
        {
            case 1:
                picked = turn++ % palette.Length;
                current = ViewTuning.ColorOf(palette[picked]);
                break;
            case 2:
                picked = random.Next(palette.Length);
                current = ViewTuning.ColorOf(palette[picked]);
                break;
            case 3:
                picked = -1;
                current = FromHsv(random.NextDouble(), saturation, brightness);
                break;
            case 4:
                picked = -1;
                hue = (hue + ViewTuning.Get(prefix + ".huestep")) % 1;
                current = FromHsv(hue, saturation, brightness);
                break;
            default:
                picked = 0;
                current = ViewTuning.ColorOf(palette[0]);
                break;
        }
        return current;
    }

    public void EndWord() { wordDone = true; }

    static Color FromHsv(double hue, double saturation, double value)
    {
        double sector = (hue % 1) * 6, rise = sector - Math.Floor(sector);
        double low = value * (1 - saturation), fall = value * (1 - saturation * rise), climb = value * (1 - saturation * (1 - rise));
        double red, green, blue;
        switch ((int)sector)
        {
            case 0: red = value; green = climb; blue = low; break;
            case 1: red = fall; green = value; blue = low; break;
            case 2: red = low; green = value; blue = climb; break;
            case 3: red = low; green = fall; blue = value; break;
            case 4: red = climb; green = low; blue = value; break;
            default: red = value; green = low; blue = fall; break;
        }
        return Color.FromRgb((byte)(red * 255), (byte)(green * 255), (byte)(blue * 255));
    }
}
