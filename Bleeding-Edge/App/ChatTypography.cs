using System;
using System.Windows;
using System.Windows.Media;

static class ChatTypography
{
    internal static readonly string Family = ChooseFamily();

    static string ChooseFamily()
    {
        foreach(string name in new[]{"Yu Mincho","Meiryo","MS Mincho","Yu Gothic"})
        {
            try
            {
                using(var family=new System.Drawing.FontFamily(name))
                {
                    if(!family.IsStyleAvailable(System.Drawing.FontStyle.Regular))continue;
                    GlyphTypeface glyph;
                    if(new Typeface(name).TryGetGlyphTypeface(out glyph) &&
                        glyph.CharacterToGlyphMap.ContainsKey(0x3042) &&
                        glyph.CharacterToGlyphMap.ContainsKey(0x30A2) &&
                        glyph.CharacterToGlyphMap.ContainsKey(0x65E5))
                        return family.Name;
                }
            }
            catch(ArgumentException) { }
        }
        return "Segoe UI";
    }

    internal static Typeface SmokeFace
    {
        get { return new Typeface(new FontFamily(Family+", Meiryo, Segoe UI Emoji, Global User Interface"),
            FontStyles.Normal, ViewTuning.Flag(WordViewMode.SumiInk,"bold") ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal); }
    }
}
