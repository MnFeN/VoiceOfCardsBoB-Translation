using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceOfCardsLocalizationTool;

internal sealed class ProjectService
{
    private readonly string root;
    private readonly string originals;
    private readonly string translation;
    private readonly string output;

    private static readonly string[] TextAssetNames =
    [
        "scenario_text",
        "card_name_text",
        "card_desc_text",
        "system_text"
    ];

    private static readonly Dictionary<string, string> TranslationFileNames = new()
    {
        { "scenario_text", "scenario.csv" },
        { "card_name_text", "card_name.csv" },
        { "card_desc_text", "card_desc.csv" },
        { "system_text", "system.csv" }
    };

    public ProjectService(string rootDirectory)
    {
        root = rootDirectory;
        originals = Path.Combine(root, "OriginalGameFiles");
        translation = Path.Combine(root, "Translation");
        output = Path.Combine(root, "GeneratedGameFiles");
    }

    public void ExportTranslationCsv()
    {
        EnsureOriginalFiles();
        Directory.CreateDirectory(translation);

        string enTextPath = Path.Combine(originals, "asset_text_en");
        string jpTextPath = Path.Combine(originals, "asset_text_jp");

        var enAssets = UnityBundleService.ReadTextAssets(enTextPath);
        var jpAssets = UnityBundleService.ReadTextAssets(jpTextPath);

        foreach (string assetName in TextAssetNames)
        {
            string enCsv = GetTextAsset(enAssets, assetName);
            string jpCsv = GetTextAsset(jpAssets, assetName);
            var enRows = ParseGameTable(enCsv, assetName + " (English)");
            var jpRows = ParseGameTable(jpCsv, assetName + " (Japanese)");
            var jpByKey = ToUniqueDictionary(jpRows, assetName + " Japanese");

            var rows = new List<TranslationRow>();
            foreach (var pair in enRows)
            {
                if (!jpByKey.TryGetValue(pair.Key, out var jp) || jp == null)
                    throw new InvalidDataException(assetName + ": Japanese resource is missing key " + pair.Key);
                rows.Add(new TranslationRow
                {
                    Key = pair.Key,
                    English = pair.Value,
                    Japanese = jp,
                    Target = pair.Value
                });
            }

            var enKeys = new HashSet<string>(enRows.Select(r => r.Key));
            var jpOnly = jpByKey.Keys.Where(k => !enKeys.Contains(k)).Take(10).ToList();
            if (jpOnly.Count > 0)
                throw new InvalidDataException(assetName + ": Japanese resource contains key(s) absent from English: " + string.Join(", ", jpOnly));

            string outPath = Path.Combine(translation, TranslationFileNames[assetName]);
            TranslationFileService.Write(outPath, rows);
            Console.WriteLine("  Generated " + Relative(outPath) + " (" + rows.Count + " rows)");
        }

        ExportLocalizationTable();
        Console.WriteLine();
        Console.WriteLine("Export complete. Target is initialized with the English text.");
    }

    public void BuildOutput()
    {
        EnsureOriginalFiles();
        EnsureTranslationFiles();
        if (Directory.Exists(output))
            Directory.Delete(output, true);
        Directory.CreateDirectory(output);

        var sourceAssets = UnityBundleService.ReadTextAssets(Path.Combine(originals, "asset_text_en"));
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string assetName in TextAssetNames)
        {
            string sourceCsv = GetTextAsset(sourceAssets, assetName);
            var sourceRows = ParseGameTable(sourceCsv, assetName + " source");
            string translationPath = Path.Combine(translation, TranslationFileNames[assetName]);
            var transRows = TranslationFileService.Read(translationPath);
            TranslationFileService.ValidateKeys(translationPath, transRows, sourceRows.Select(r => r.Key));

            var sourceByKey = ToUniqueDictionary(sourceRows, assetName + " source");
            ValidatePlaceholders(translationPath, transRows);

            var targetByKey = transRows.ToDictionary(r => r.Key, r => r.Target);
            var finalRows = sourceRows.Select(r => new KeyValuePair<string, string>(r.Key, targetByKey[r.Key])).ToList();
            replacements[assetName] = CsvCodec.SerializeTwoColumn(finalRows);
        }

        string outText = Path.Combine(output, "asset_text_en");
        UnityBundleService.WriteTextAssets(Path.Combine(originals, "asset_text_en"), outText, replacements);
        Console.WriteLine("  Generated " + Relative(outText));

        BuildLocalizationTable();
        BuildFont();
        Console.WriteLine();
        Console.WriteLine("Build complete. Files are ready for the patcher to be published");
    }

    private void ExportLocalizationTable()
    {
        string sharedPath = Path.Combine(originals, "localization-assets-shared_assets_all.bundle");
        string enPath = Path.Combine(originals, "localization-string-tables-english(en)_assets_all.bundle");
        string jpPath = Path.Combine(originals, "localization-string-tables-japanese(ja)_assets_all.bundle");

        var shared = UnityBundleService.ReadSharedTable(sharedPath);
        var en = UnityBundleService.ReadStringTable(enPath, "System_en");
        var jp = UnityBundleService.ReadStringTable(jpPath, "System_ja");

        var rows = new List<TranslationRow>();
        foreach (var entry in shared)
        {
            if (!en.TryGetValue(entry.Id, out var enValue) || enValue == null ||
                !jp.TryGetValue(entry.Id, out var jpValue) || jpValue == null)
                throw new InvalidDataException("Localization table is missing shared id " + entry.Id + " (" + entry.Key + ").");
            rows.Add(new TranslationRow
            {
                Key = entry.Key,
                English = enValue,
                Japanese = jpValue,
                Target = enValue
            });
        }

        string outPath = Path.Combine(translation, "localization_system.csv");
        TranslationFileService.Write(outPath, rows);
        Console.WriteLine("  Generated " + Relative(outPath) + " (" + rows.Count + " rows)");
    }

    private void BuildLocalizationTable()
    {
        string sharedPath = Path.Combine(originals, "localization-assets-shared_assets_all.bundle");
        string enSource = Path.Combine(originals, "localization-string-tables-english(en)_assets_all.bundle");
        string csvPath = Path.Combine(translation, "localization_system.csv");
        var shared = UnityBundleService.ReadSharedTable(sharedPath);
        var en = UnityBundleService.ReadStringTable(enSource, "System_en");
        var rows = TranslationFileService.Read(csvPath);
        TranslationFileService.ValidateKeys(csvPath, rows, shared.Select(e => e.Key));

        var idByKey = shared.ToDictionary(e => e.Key, e => e.Id);
        ValidatePlaceholders(csvPath, rows);

        var replacements = rows.ToDictionary(r => idByKey[r.Key], r => r.Target);
        string outPath = Path.Combine(output, "localization-string-tables-english(en)_assets_all.bundle");
        UnityBundleService.WriteStringTable(enSource, outPath, "System_en", replacements);
        Console.WriteLine("  Generated " + Relative(outPath));
    }

    private void BuildFont()
    {
        string[] fonts = Directory
            .EnumerateFiles(translation)
            .Where(path =>
                Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (fonts.Length == 0)
            throw new FileNotFoundException("No .otf or .ttf font was found in Translation.");
        if (fonts.Length > 1)
            throw new InvalidDataException("Translation must contain exactly one .otf or .ttf font.");

        string[] csvFiles =
        [
            "scenario.csv",
            "card_name.csv",
            "card_desc.csv",
            "system.csv",
            "localization_system.csv"
        ];

        var texts = new List<string>();
        foreach (string csvFile in csvFiles)
        {
            string csvPath = Path.Combine(translation, csvFile);
            foreach (var row in TranslationFileService.Read(csvPath))
                texts.Add(row.Target);
        }

        string sourceAssetsPath = Path.Combine(originals, "sharedassets0.assets");
        string sourceResSPath = Path.Combine(originals, "sharedassets0.assets.resS");
        string outputAssetsPath = Path.Combine(output, "sharedassets0.assets");
        string outputResSPath = Path.Combine(output, "sharedassets0.assets.resS");

        FontService.Build(
            sourceAssetsPath,
            sourceResSPath,
            fonts[0],
            texts,
            outputAssetsPath,
            outputResSPath);

        Console.WriteLine("  Generated " + Relative(outputAssetsPath));
        Console.WriteLine("  Generated " + Relative(outputResSPath));
    }

    private static void ValidatePlaceholders(string path, IList<TranslationRow> rows)
    {
        foreach (var row in rows)
        {
            var sourceTokens = ExtractProtectedTokens(row.English);
            var targetTokens = ExtractProtectedTokens(row.Target);
            foreach (var token in sourceTokens)
            {
                if (!targetTokens.Contains(token))
                    Console.WriteLine("  Warning: " + Path.GetFileName(path) + " key " + row.Key + " may be missing token " + token);
            }
        }
    }

    private static HashSet<string> ExtractProtectedTokens(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
            return result;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                int end = text.IndexOf('}', i + 1);
                if (end > i)
                {
                    result.Add(text.Substring(i, end - i + 1));
                    i = end;
                }
            }
            else if (text[i] == '<')
            {
                int end = text.IndexOf('>', i + 1);
                if (end > i)
                {
                    result.Add(text.Substring(i, end - i + 1));
                    i = end;
                }
            }
        }
        return result;
    }

    private static List<KeyValuePair<string, string>> ParseGameTable(string text, string name)
    {
        var csv = CsvCodec.Parse(text);
        var rows = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < csv.Count; i++)
        {
            if (csv[i].Count == 1 && csv[i][0].Length == 0)
                continue;
            if (csv[i].Count != 2)
                throw new InvalidDataException(name + ": logical row " + (i + 1) + " has " + csv[i].Count + " columns; expected 2.");
            rows.Add(new KeyValuePair<string, string>(csv[i][0], csv[i][1]));
        }
        return rows;
    }

    private static Dictionary<string, string> ToUniqueDictionary(IEnumerable<KeyValuePair<string, string>> rows, string name)
    {
        var dict = new Dictionary<string, string>();
        foreach (var row in rows)
        {
            if (dict.ContainsKey(row.Key))
                throw new InvalidDataException(name + " contains duplicate key " + row.Key);
            dict.Add(row.Key, row.Value);
        }
        return dict;
    }

    private static string GetTextAsset(Dictionary<string, string> assets, string baseName)
    {
        if (assets.TryGetValue(baseName, out var value) && value != null)
            return value;
        if (assets.TryGetValue(baseName + ".csv", out value) && value != null)
            return value;
        throw new InvalidDataException("TextAsset not found: " + baseName);
    }

    private void EnsureOriginalFiles()
    {
        string[] required =
        [
            "asset_text_en",
            "asset_text_jp",
            "localization-assets-shared_assets_all.bundle",
            "localization-string-tables-english(en)_assets_all.bundle",
            "localization-string-tables-japanese(ja)_assets_all.bundle",
            "sharedassets0.assets",
            "sharedassets0.assets.resS"
        ];
        foreach (string name in required)
        {
            string path = Path.Combine(originals, name);
            if (!File.Exists(path))
                throw new FileNotFoundException("Required original game file is missing.", path);
        }
    }

    private void EnsureTranslationFiles()
    {
        string[] required =
        [
            "scenario.csv", "card_name.csv", "card_desc.csv", "system.csv", "localization_system.csv"
        ];
        foreach (string name in required)
        {
            string path = Path.Combine(translation, name);
            if (!File.Exists(path))
                throw new FileNotFoundException("Translation file is missing. Run option 1 first.", path);
        }
    }

    private string Relative(string path)
    {
        return path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}