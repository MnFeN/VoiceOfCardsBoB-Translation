using AssetsTools.NET;
using AssetsTools.NET.Extra;
using SkiaSharp;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceOfCardsLocalizationTool;

internal static class FontService
{
    // Voice of Cards: The Beasts of Burden / Unity 2019.4.40f1
    // Font_CardAnalogica in sharedassets0.assets.
    private const long FontPathId = 2847;
    private const string FontAssetName = "Font_CardAnalogica";

    // Serialized TMP_FontAsset layout verified from this game's PathID 2847.
    private const int GlyphCountOffset = 0xF0;
    private const int GlyphTableOffset = 0xF4;
    private const int GlyphRecordSize = 48;
    private const int CharacterRecordSize = 16;

    // Font_CardAnalogica Atlas: 4096 x 4096, Alpha8, external data in sharedassets0.assets.resS.
    private const long AtlasResourceOffset = 0x01937E84;
    private const int AtlasWidth = 4096;
    private const int AtlasHeight = 4096;

    // Parameters used by the successful in-game "新游戏" test.
    private const int SourcePixelSize = 38;
    private const int SdfScale = 8;
    private const double SdfSpread = 4.0;
    private const uint NewHanTemplateUnicode = 0x65B0; // 新

    private static readonly HashSet<int> ChinesePunctuation =
        "，。！？：；、（）［］【】《》〈〉「」『』“”‘’—…·"
            .EnumerateRunes()
            .Select(r => r.Value)
            .ToHashSet();

    public static void Build(
        string sourceAssetsPath,
        string sourceResSPath,
        string fontPath,
        IEnumerable<string> texts,
        string outputAssetsPath,
        string outputResSPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAssetsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceResSPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputAssetsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputResSPath);

        if (!File.Exists(sourceAssetsPath))
        {
            throw new FileNotFoundException("Original sharedassets0.assets was not found.", sourceAssetsPath);
        }

        if (!File.Exists(sourceResSPath))
        {
            throw new FileNotFoundException("Original sharedassets0.assets.resS was not found.", sourceResSPath);
        }

        if (!File.Exists(fontPath))
            throw new FileNotFoundException("Translation font was not found.", fontPath);

        if (Path.GetFullPath(sourceAssetsPath).Equals(
                Path.GetFullPath(outputAssetsPath), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(sourceResSPath).Equals(
                Path.GetFullPath(outputResSPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Font output paths must not overwrite OriginalGameFiles directly.");
        }

        SortedSet<int> replacementCodepoints = CollectReplacementCodepoints(texts);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputAssetsPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputResSPath))!);

        if (replacementCodepoints.Count == 0)
        {
            File.Copy(sourceAssetsPath, outputAssetsPath, true);
            File.Copy(sourceResSPath, outputResSPath, true);
            return;
        }

        using SKTypeface typeface = SKTypeface.FromFile(fontPath)
            ?? throw new InvalidDataException(
                "The translation font could not be opened as an OTF/TTF font: " + fontPath);

        using var renderFont = new SKFont(typeface, SourcePixelSize * SdfScale)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.None,
            Subpixel = false,
            LinearMetrics = true,
            EmbeddedBitmaps = false
        };

        var missingFromSourceFont = replacementCodepoints.Where(cp => renderFont.GetGlyph(cp) == 0).ToList();

        if (missingFromSourceFont.Count > 0)
        {
            throw new InvalidDataException(
                "The translation font does not contain: " +
                string.Join(", ", missingFromSourceFont.Take(20).Select(FormatCodepoint)) +
                (missingFromSourceFont.Count > 20
                    ? $" ... ({missingFromSourceFont.Count} total)"
                    : string.Empty));
        }

        var manager = new AssetsManager();
        var assetsInstance = manager.LoadAssetsFile(sourceAssetsPath, false);

        try
        {
            AssetsFile assetsFile = assetsInstance.file;

            if (!string.Equals(assetsFile.Metadata.UnityVersion, "2019.4.40f1", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unexpected Unity version in sharedassets0.assets: " +
                    $"{assetsFile.Metadata.UnityVersion}. Expected 2019.4.40f1.");
            }

            AssetFileInfo fontInfo = assetsFile.GetAssetInfo(FontPathId)
                ?? throw new InvalidDataException(
                    $"Could not find Font_CardAnalogica PathID {FontPathId}.");

            byte[] originalFontData = ReadRawAsset(assetsFile, fontInfo);

            if (!ContainsAscii(originalFontData, FontAssetName))
            {
                throw new InvalidDataException($"PathID {FontPathId} does not look like {FontAssetName}.");
            }

            FontData fontData = FontData.Parse(originalFontData);
            var glyphByIndex = fontData.Glyphs.ToDictionary(g => g.Index);
            var originalCharactersByGlyph = fontData.Characters.GroupBy(c => c.GlyphIndex).ToDictionary(g => g.Key, g => g.ToList());

            var requiredUnicodes = replacementCodepoints.Select(cp => checked((uint)cp)).ToHashSet();

            CharacterRecord templateCharacter = fontData.Characters
                .FirstOrDefault(c => c.Unicode == NewHanTemplateUnicode)
                ?? throw new InvalidDataException(
                    "Font_CardAnalogica does not contain the template character 新 (U+65B0).");

            if (!glyphByIndex.TryGetValue(templateCharacter.GlyphIndex, out var templateGlyph))
            {
                throw new InvalidDataException("Font_CardAnalogica does not contain the template glyph for 新 (U+65B0).");
            }

            List<GlyphRecord> recyclableGlyphs = fontData.Glyphs
                .Where(g => g.Index != templateGlyph.Index)
                .Where(g => g.AtlasIndex == 0 && g.RectWidth > 0 && g.RectHeight > 0)
                .Where(g => !originalCharactersByGlyph.TryGetValue(g.Index, out var references) ||
                    references.All(c => IsRecyclableOriginalCodepoint(c.Unicode) &&
                        !requiredUnicodes.Contains(c.Unicode)))
                .ToList();

            // Recyclable mappings are removed only when their atlas slot is actually consumed.
            // Unused Japanese glyphs therefore remain intact whenever there is room to keep them.
            var recyclePool = new RecycleGlyphPool(recyclableGlyphs, fontData.Characters);
            var characterByUnicode = fontData.Characters.ToDictionary(c => c.Unicode);
            var claimedOriginalGlyphs = new HashSet<uint>();
            byte[] atlas = ReadAtlas(sourceResSPath);

            int replaced = 0;
            int added = 0;
            int recycled = 0;
            int detached = 0;

            foreach (int codepoint in replacementCodepoints)
            {
                uint unicode = checked((uint)codepoint);

                if (characterByUnicode.TryGetValue(unicode, out CharacterRecord? character))
                {
                    if (!glyphByIndex.TryGetValue(character.GlyphIndex, out var originalGlyph))
                    {
                        throw new InvalidDataException(
                            $"Character {FormatCodepoint(codepoint)} refers to missing " +
                            $"GlyphIndex {character.GlyphIndex}.");
                    }

                    bool hasProtectedAlias = originalCharactersByGlyph
                        .GetValueOrDefault(originalGlyph.Index, [])
                        .Any(other => other.Unicode != unicode && !IsRecyclableOriginalCodepoint(other.Unicode));

                    bool canOverwriteOriginal = !hasProtectedAlias && claimedOriginalGlyphs.Add(originalGlyph.Index);

                    GlyphRecord targetGlyph = originalGlyph;

                    if (!canOverwriteOriginal)
                    {
                        targetGlyph = recyclePool.Take(originalGlyph, codepoint);

                        character.GlyphIndex = targetGlyph.Index;
                        recycled++;
                        detached++;
                    }

                    byte[] sdfResult = RenderSdf(renderFont, codepoint, targetGlyph.RectWidth, targetGlyph.RectHeight);
                    WriteGlyphToAtlas(atlas, targetGlyph, sdfResult);

                    replaced++;
                    continue;
                }

                if (!IsHan(codepoint))
                {
                    throw new InvalidDataException(
                        $"Selected punctuation {FormatCodepoint(codepoint)} is missing from " +
                        "Font_CardAnalogica. Only missing Han characters may be created.");
                }

                GlyphRecord addedGlyph = recyclePool.Take(templateGlyph, codepoint);

                var addedCharacter = new CharacterRecord
                {
                    ElementType = templateCharacter.ElementType,
                    Unicode = unicode,
                    GlyphIndex = addedGlyph.Index,
                    Scale = templateCharacter.Scale
                };

                fontData.Characters.Add(addedCharacter);
                characterByUnicode.Add(unicode, addedCharacter);

                byte[] sdf = RenderSdf(renderFont, codepoint, addedGlyph.RectWidth, addedGlyph.RectHeight);
                WriteGlyphToAtlas(atlas, addedGlyph, sdf);

                added++;
                recycled++;
            }

            fontData.Glyphs.Sort((a, b) => a.Index.CompareTo(b.Index));
            fontData.Characters.Sort((a, b) => a.Unicode.CompareTo(b.Unicode));

            byte[] rebuiltFontData = fontData.Serialize();
            fontInfo.SetNewData(rebuiltFontData);

            if (File.Exists(outputAssetsPath))
                File.Delete(outputAssetsPath);

            using (var writer = new AssetsFileWriter(outputAssetsPath))
                assetsFile.Write(writer);

            WriteAtlas(sourceResSPath, outputResSPath, atlas);

            Console.WriteLine($"  Font characters replaced: {replaced}");
            Console.WriteLine($"  Font characters added: {added}");
            Console.WriteLine($"  Existing characters detached from shared glyphs: {detached}");
            Console.WriteLine($"  Recycled original CJK glyph slots used: {recycled}");
            Console.WriteLine($"  Recycled original CJK glyph slots remaining: {recyclePool.Count}");
        }
        finally
        {
            manager.UnloadAll();
        }
    }

    private sealed class RecycleGlyphPool
    {
        private readonly List<GlyphRecord> glyphs;
        private readonly List<CharacterRecord> characters;

        public RecycleGlyphPool(List<GlyphRecord> glyphs, List<CharacterRecord> characters)
        {
            this.glyphs = glyphs;
            this.characters = characters;
        }

        public int Count => glyphs.Count;

        public GlyphRecord Take(GlyphRecord metricTemplate, int codepoint)
        {
            int bestIndex = FindBest(metricTemplate, exactSizeOnly: true);

            if (bestIndex < 0)
                bestIndex = FindBest(metricTemplate, exactSizeOnly: false);

            if (bestIndex < 0)
            {
                throw new InvalidDataException(
                    $"No recyclable original CJK glyph slot can fit {FormatCodepoint(codepoint)} " +
                    $"({metricTemplate.RectWidth}x{metricTemplate.RectHeight}). " +
                    "The build no longer depends on unoccupied atlas space; this means the " +
                    "original font has no unused " +
                    "CJK glyph rectangle large enough for another translated character.");
            }

            GlyphRecord glyph = glyphs[bestIndex];
            int lastIndex = glyphs.Count - 1;
            glyphs[bestIndex] = glyphs[lastIndex];
            glyphs.RemoveAt(lastIndex);
            characters.RemoveAll(c => c.GlyphIndex == glyph.Index);
            glyph.CopyMetricsFrom(metricTemplate);
            return glyph;
        }

        private int FindBest(GlyphRecord template, bool exactSizeOnly)
        {
            int bestIndex = -1;
            int bestWaste = int.MaxValue;

            for (int i = 0; i < glyphs.Count; i++)
            {
                GlyphRecord glyph = glyphs[i];

                if (exactSizeOnly)
                {
                    if (glyph.RectWidth != template.RectWidth || glyph.RectHeight != template.RectHeight)
                        continue;

                    return i;
                }

                if (glyph.RectWidth < template.RectWidth || glyph.RectHeight < template.RectHeight)
                    continue;

                int waste = glyph.RectWidth * glyph.RectHeight -
                    template.RectWidth * template.RectHeight;

                if (waste >= bestWaste)
                    continue;

                bestWaste = waste;
                bestIndex = i;
            }

            return bestIndex;
        }
    }

    private static bool IsRecyclableOriginalCodepoint(uint unicode)
    {
        int codepoint = checked((int)unicode);
        return IsHan(codepoint) || IsJapaneseSyllabary(codepoint);
    }

    private static bool IsJapaneseSyllabary(int codepoint)
    {
        return codepoint is >= 0x3040 and <= 0x30FF || codepoint is >= 0x31F0 and <= 0x31FF || codepoint is >= 0xFF65 and <= 0xFF9F;
    }

    private static SortedSet<int> CollectReplacementCodepoints(IEnumerable<string> texts)
    {
        var result = new SortedSet<int>();

        foreach (string? text in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;

            foreach (Rune rune in text.EnumerateRunes())
            {
                int codepoint = rune.Value;

                if (IsHan(codepoint) || ChinesePunctuation.Contains(codepoint))
                    result.Add(codepoint);
            }
        }

        return result;
    }

    private static bool IsHan(int codepoint)
    {
        return codepoint is >= 0x3400 and <= 0x4DBF ||
               codepoint is >= 0x4E00 and <= 0x9FFF ||
               codepoint is >= 0xF900 and <= 0xFAFF ||
               codepoint is >= 0x20000 and <= 0x2A6DF ||
               codepoint is >= 0x2A700 and <= 0x2B73F ||
               codepoint is >= 0x2B740 and <= 0x2B81F ||
               codepoint is >= 0x2B820 and <= 0x2CEAF ||
               codepoint is >= 0x2CEB0 and <= 0x2EBEF ||
               codepoint is >= 0x30000 and <= 0x3134F;
    }

    private static byte[] ReadRawAsset(AssetsFile assetsFile, AssetFileInfo info)
    {
        int length = checked((int)info.ByteSize);
        byte[] data = new byte[length];
        AssetsFileReader reader = assetsFile.Reader;
        long originalPosition = reader.Position;

        try
        {
            reader.Position = info.GetAbsoluteByteOffset(assetsFile);
            reader.Read(data, 0, data.Length);
            return data;
        }
        finally
        {
            reader.Position = originalPosition;
        }
    }

    private static byte[] ReadAtlas(string resSPath)
    {
        long requiredLength = AtlasResourceOffset + (long)AtlasWidth * AtlasHeight;
        var info = new FileInfo(resSPath);

        if (info.Length < requiredLength)
        {
            throw new InvalidDataException(
                $"sharedassets0.assets.resS is too small for the expected Font_CardAnalogica " +
                $"atlas. Expected at least {requiredLength} bytes, got {info.Length}.");
        }

        byte[] atlas = new byte[AtlasWidth * AtlasHeight];
        using var stream = new FileStream(resSPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        stream.Position = AtlasResourceOffset;
        stream.ReadExactly(atlas);
        return atlas;
    }

    private static void WriteAtlas(string sourceResSPath, string outputResSPath, byte[] atlas)
    {
        File.Copy(sourceResSPath, outputResSPath, true);

        using var stream = new FileStream(outputResSPath, FileMode.Open, FileAccess.Write, FileShare.None);

        stream.Position = AtlasResourceOffset;
        stream.Write(atlas);
    }

    private static void WriteGlyphToAtlas(byte[] atlas, GlyphRecord glyph, byte[] sdf)
    {
        if (glyph.AtlasIndex != 0)
        {
            throw new InvalidDataException($"GlyphIndex {glyph.Index} uses unsupported atlas index {glyph.AtlasIndex}.");
        }

        if (glyph.RectWidth <= 0 || glyph.RectHeight <= 0)
        {
            throw new InvalidDataException($"GlyphIndex {glyph.Index} has an empty atlas rectangle.");
        }

        if (sdf.Length != glyph.RectWidth * glyph.RectHeight)
        {
            throw new ArgumentException("SDF dimensions do not match the glyph rectangle.", nameof(sdf));
        }

        if (glyph.RectX < 0 || glyph.RectY < 0 || glyph.RectX + glyph.RectWidth > AtlasWidth || glyph.RectY + glyph.RectHeight > AtlasHeight)
        {
            throw new InvalidDataException($"GlyphIndex {glyph.Index} has an atlas rectangle outside the 4096x4096 texture.");
        }

        // SDF is top-down. Unity's raw Alpha8 texture rows are addressed bottom-up relative to
        // the glyph coordinates used by TMP.
        for (int row = 0; row < glyph.RectHeight; row++)
        {
            int sourceRow = glyph.RectHeight - 1 - row;
            int sourceOffset = sourceRow * glyph.RectWidth;
            int destinationOffset = (glyph.RectY + row) * AtlasWidth + glyph.RectX;

            Buffer.BlockCopy(sdf, sourceOffset, atlas, destinationOffset, glyph.RectWidth);
        }
    }

    private static byte[] RenderSdf(SKFont font, int codepoint, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        ushort glyphId = font.GetGlyph(codepoint);

        if (glyphId == 0)
        {
            throw new InvalidDataException("Source font has no glyph for " + FormatCodepoint(codepoint));
        }

        int highWidth = checked(width * SdfScale);
        int highHeight = checked(height * SdfScale);
        string text = new Rune(codepoint).ToString();

        using var bitmap = new SKBitmap(new SKImageInfo(highWidth, highHeight, SKColorType.Bgra8888, SKAlphaType.Premul));

        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        canvas.Clear(SKColors.Transparent);

        font.MeasureText(text, out SKRect bounds, paint);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException("Source font returned invalid bounds for " + FormatCodepoint(codepoint));
        }

        float x = (highWidth - bounds.Width) * 0.5f - bounds.Left;
        float y = (highHeight - bounds.Height) * 0.5f - bounds.Top;

        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);

        canvas.Flush();

        int rowBytes = bitmap.RowBytes;
        byte[] bgra = new byte[checked(rowBytes * highHeight)];
        Marshal.Copy(bitmap.GetPixels(), bgra, 0, bgra.Length);

        byte[] mask = new byte[highWidth * highHeight];
        bool anyInside = false;
        bool anyOutside = false;
        byte maximumCoverage = 0;

        for (int yPixel = 0; yPixel < highHeight; yPixel++)
        {
            int sourceRow = yPixel * rowBytes;
            int targetRow = yPixel * highWidth;

            for (int xPixel = 0; xPixel < highWidth; xPixel++)
            {
                byte coverage = bgra[sourceRow + xPixel * 4 + 3];
                mask[targetRow + xPixel] = coverage;

                if (coverage > maximumCoverage)
                    maximumCoverage = coverage;

                if (coverage >= 128)
                    anyInside = true;
                else
                    anyOutside = true;
            }
        }

        if (!anyInside)
        {
            throw new InvalidDataException(
                $"Could not rasterize {FormatCodepoint(codepoint)}. " +
                $"Maximum coverage was {maximumCoverage}; bounds were " +
                $"{bounds.Left:F2},{bounds.Top:F2},{bounds.Right:F2},{bounds.Bottom:F2}.");
        }

        if (!anyOutside)
        {
            throw new InvalidDataException($"Rasterized glyph filled the entire bitmap for {FormatCodepoint(codepoint)}.");
        }

        double[] distanceToOutside = DistanceToFeature(mask, highWidth, highHeight, featureInside: false);

        double[] distanceToInside = DistanceToFeature(mask, highWidth, highHeight, featureInside: true);

        double scale = 127.0 / (SdfSpread * SdfScale);
        double[] highSdf = new double[mask.Length];

        for (int i = 0; i < mask.Length; i++)
        {
            bool inside = mask[i] >= 128;
            double insideDistance = inside ? distanceToOutside[i] : 0.0;
            double outsideDistance = inside ? 0.0 : distanceToInside[i];
            double signedDistance = insideDistance - outsideDistance + mask[i] / 255.0 - 0.5;

            highSdf[i] = Math.Clamp(128.0 + signedDistance * scale, 0.0, 255.0);
        }

        byte[] result = new byte[width * height];
        double divisor = SdfScale * SdfScale;

        for (int yPixel = 0; yPixel < height; yPixel++)
        {
            for (int xPixel = 0; xPixel < width; xPixel++)
            {
                double sum = 0;
                int highY = yPixel * SdfScale;
                int highX = xPixel * SdfScale;

                for (int sy = 0; sy < SdfScale; sy++)
                {
                    int row = (highY + sy) * highWidth + highX;

                    for (int sx = 0; sx < SdfScale; sx++)
                        sum += highSdf[row + sx];
                }

                result[yPixel * width + xPixel] = (byte)Math.Clamp((int)Math.Round(sum / divisor), 0, 255);
            }
        }

        return result;
    }

    private static double[] DistanceToFeature(byte[] mask, int width, int height, bool featureInside)
    {
        int length = checked(width * height);
        double[] temp = new double[length];
        double[] result = new double[length];
        int max = Math.Max(width, height);
        double[] f = new double[max];
        double[] d = new double[max];
        int[] v = new int[max];
        double[] z = new double[max + 1];
        const double infinity = 1e12;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool inside = mask[y * width + x] >= 128;
                f[y] = inside == featureInside ? 0.0 : infinity;
            }

            DistanceTransform1D(f, d, v, z, height);

            for (int y = 0; y < height; y++)
                temp[y * width + x] = d[y];
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * width;

            for (int x = 0; x < width; x++)
                f[x] = temp[row + x];

            DistanceTransform1D(f, d, v, z, width);

            for (int x = 0; x < width; x++)
                result[row + x] = Math.Sqrt(d[x]);
        }

        return result;
    }

    // Felzenszwalb / Huttenlocher exact squared Euclidean distance transform.
    private static void DistanceTransform1D(double[] f, double[] d, int[] v, double[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            double s;

            while (true)
            {
                int vk = v[k];
                s = ((f[q] + (double)q * q) - (f[vk] + (double)vk * vk)) /
                    (2.0 * (q - vk));

                if (s > z[k])
                    break;

                k--;
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;

        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;

            double delta = q - v[k];
            d[q] = delta * delta + f[v[k]];
        }
    }

    private static bool ContainsAscii(byte[] data, string text)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text);
        return data.AsSpan().IndexOf(needle) >= 0;
    }

    private static string FormatCodepoint(int codepoint)
    {
        string text = Rune.IsValid(codepoint) ? new Rune(codepoint).ToString() : "?";
        return $"{text} (U+{codepoint:X4})";
    }

    private sealed class FontData
    {
        public required byte[] Prefix { get; init; }
        public required List<GlyphRecord> Glyphs { get; init; }
        public required List<CharacterRecord> Characters { get; init; }
        public required byte[] Suffix { get; init; }

        public static FontData Parse(byte[] data)
        {
            if (data.Length < GlyphTableOffset + 4)
            {
                throw new InvalidDataException("Font_CardAnalogica object is unexpectedly small.");
            }

            uint glyphCount = ReadUInt32(data, GlyphCountOffset);
            long characterCountOffsetLong = GlyphTableOffset + (long)glyphCount * GlyphRecordSize;

            if (characterCountOffsetLong < 0 || characterCountOffsetLong + 4 > data.Length)
                throw new InvalidDataException("Invalid TMP glyph table length.");

            int characterCountOffset = checked((int)characterCountOffsetLong);
            uint characterCount = ReadUInt32(data, characterCountOffset);
            long characterTableOffsetLong = characterCountOffsetLong + 4;
            long suffixOffsetLong = characterTableOffsetLong + (long)characterCount * CharacterRecordSize;

            if (suffixOffsetLong < 0 || suffixOffsetLong > data.Length)
                throw new InvalidDataException("Invalid TMP character table length.");

            int characterTableOffset = checked((int)characterTableOffsetLong);
            int suffixOffset = checked((int)suffixOffsetLong);
            var glyphs = new List<GlyphRecord>(checked((int)glyphCount));

            for (int i = 0; i < glyphCount; i++)
            {
                glyphs.Add(GlyphRecord.Read(data, GlyphTableOffset + i * GlyphRecordSize));
            }

            var characters = new List<CharacterRecord>(checked((int)characterCount));

            for (int i = 0; i < characterCount; i++)
            {
                characters.Add(CharacterRecord.Read(data, characterTableOffset + i * CharacterRecordSize));
            }

            if (glyphs.Select(g => g.Index).Distinct().Count() != glyphs.Count)
                throw new InvalidDataException("Font_CardAnalogica contains duplicate glyph indexes.");

            if (characters.Select(c => c.Unicode).Distinct().Count() != characters.Count)
            {
                throw new InvalidDataException("Font_CardAnalogica contains duplicate Unicode character records.");
            }

            return new FontData
            {
                Prefix = data[..GlyphCountOffset],
                Glyphs = glyphs,
                Characters = characters,
                Suffix = data[suffixOffset..]
            };
        }

        public byte[] Serialize()
        {
            using var stream = new MemoryStream(
                Prefix.Length +
                4 +
                Glyphs.Count * GlyphRecordSize +
                4 +
                Characters.Count * CharacterRecordSize +
                Suffix.Length);

            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Prefix);
            writer.Write(checked((uint)Glyphs.Count));

            foreach (GlyphRecord glyph in Glyphs)
                glyph.Write(writer);

            writer.Write(checked((uint)Characters.Count));

            foreach (CharacterRecord character in Characters)
                character.Write(writer);

            writer.Write(Suffix);
            writer.Flush();
            return stream.ToArray();
        }
    }

    private sealed class GlyphRecord
    {
        public uint Index { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float HorizontalBearingX { get; set; }
        public float HorizontalBearingY { get; set; }
        public float HorizontalAdvance { get; set; }
        public int RectX { get; set; }
        public int RectY { get; set; }
        public int RectWidth { get; set; }
        public int RectHeight { get; set; }
        public float Scale { get; set; }
        public uint AtlasIndex { get; set; }

        public static GlyphRecord Read(byte[] data, int offset)
        {
            return new GlyphRecord
            {
                Index = ReadUInt32(data, offset),
                Width = ReadSingle(data, offset + 4),
                Height = ReadSingle(data, offset + 8),
                HorizontalBearingX = ReadSingle(data, offset + 12),
                HorizontalBearingY = ReadSingle(data, offset + 16),
                HorizontalAdvance = ReadSingle(data, offset + 20),
                RectX = ReadInt32(data, offset + 24),
                RectY = ReadInt32(data, offset + 28),
                RectWidth = ReadInt32(data, offset + 32),
                RectHeight = ReadInt32(data, offset + 36),
                Scale = ReadSingle(data, offset + 40),
                AtlasIndex = ReadUInt32(data, offset + 44)
            };
        }

        public void CopyMetricsFrom(GlyphRecord source)
        {
            Width = source.Width;
            Height = source.Height;
            HorizontalBearingX = source.HorizontalBearingX;
            HorizontalBearingY = source.HorizontalBearingY;
            HorizontalAdvance = source.HorizontalAdvance;
            Scale = source.Scale;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(Index);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write(HorizontalBearingX);
            writer.Write(HorizontalBearingY);
            writer.Write(HorizontalAdvance);
            writer.Write(RectX);
            writer.Write(RectY);
            writer.Write(RectWidth);
            writer.Write(RectHeight);
            writer.Write(Scale);
            writer.Write(AtlasIndex);
        }
    }

    private sealed class CharacterRecord
    {
        public uint ElementType { get; set; }
        public uint Unicode { get; set; }
        public uint GlyphIndex { get; set; }
        public float Scale { get; set; }

        public static CharacterRecord Read(byte[] data, int offset)
        {
            return new CharacterRecord
            {
                ElementType = ReadUInt32(data, offset),
                Unicode = ReadUInt32(data, offset + 4),
                GlyphIndex = ReadUInt32(data, offset + 8),
                Scale = ReadSingle(data, offset + 12)
            };
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(ElementType);
            writer.Write(Unicode);
            writer.Write(GlyphIndex);
            writer.Write(Scale);
        }
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static float ReadSingle(byte[] data, int offset)
    {
        int bits = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle(bits);
    }
}


