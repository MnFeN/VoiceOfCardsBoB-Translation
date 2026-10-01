using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace VoiceOfCardsLocalizationTool;

internal static class UnityBundleService
{
    public static Dictionary<string, string> ReadTextAssets(string bundlePath)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        var manager = new AssetsManager();
        var bundle = manager.LoadBundleFile(bundlePath, true);

        try
        {
            var assetsInstance = manager.LoadAssetsFileFromBundle(bundle, 0, false);

            var assetsFile = assetsInstance.file;

            foreach (var info in assetsFile.GetAssetsOfType(AssetClassID.TextAsset))
            {
                var baseField = manager.GetBaseField(assetsInstance, info);

                var name = baseField["m_Name"].AsString;
                var script = baseField["m_Script"].AsString;

                result[name] = script;
            }
        }
        finally
        {
            manager.UnloadAll();
        }

        return result;
    }

    public static void WriteTextAssets(
        string sourceBundlePath,
        string outputBundlePath,
        IDictionary<string, string> replacements)
    {
        var manager = new AssetsManager();
        var bundle = manager.LoadBundleFile(sourceBundlePath, true);

        try
        {
            var assetsInstance =
                manager.LoadAssetsFileFromBundle(bundle, 0, false);

            var assetsFile = assetsInstance.file;

            var foundNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var info in assetsFile.GetAssetsOfType(AssetClassID.TextAsset))
            {
                var baseField = manager.GetBaseField(assetsInstance, info);
                var name = baseField["m_Name"].AsString;

                foundNames.Add(name);

                if (!replacements.TryGetValue(name, out var replacement))
                    continue;

                baseField["m_Script"].AsString = replacement;

                info.SetNewData(baseField);
            }

            var missing = replacements.Keys
                .Where(key => !foundNames.Contains(key))
                .ToList();

            if (missing.Count > 0)
            {
                throw new InvalidDataException(
                    "TextAsset(s) not found in source bundle: " +
                    string.Join(", ", missing));
            }

            ReplaceAssetsFileInBundle(bundle, assetsFile);

            WriteBundle(bundle, outputBundlePath);
        }
        finally
        {
            manager.UnloadAll();
        }
    }

    public static List<LocalizedEntry> ReadSharedTable(string sharedBundlePath)
    {
        var manager = new AssetsManager();
        var bundle = manager.LoadBundleFile(sharedBundlePath, true);

        try
        {
            var assetsInstance = manager.LoadAssetsFileFromBundle(bundle, 0, false);

            foreach (var info in assetsInstance.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var baseField = manager.GetBaseField(assetsInstance, info);

                if (!string.Equals(baseField["m_Name"].AsString, "System Shared Data", StringComparison.Ordinal))
                {
                    continue;
                }

                var array = baseField["m_Entries.Array"];

                var result = new List<LocalizedEntry>();

                foreach (var entry in array.Children)
                {
                    result.Add(new LocalizedEntry
                    {
                        Id = entry["m_Id"].AsLong,
                        Key = entry["m_Key"].AsString,
                        Value = null
                    });
                }

                return result;
            }

            throw new InvalidDataException($"Could not find 'System Shared Data' in {sharedBundlePath}");
        }
        finally
        {
            manager.UnloadAll();
        }
    }

    public static Dictionary<long, string> ReadStringTable(string bundlePath, string tableName)
    {
        var manager = new AssetsManager();
        var bundle = manager.LoadBundleFile(bundlePath, true);

        try
        {
            var assetsInstance = manager.LoadAssetsFileFromBundle(bundle, 0, false);

            foreach (var info in assetsInstance.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var baseField = manager.GetBaseField(assetsInstance, info);

                if (!string.Equals(baseField["m_Name"].AsString, tableName, StringComparison.Ordinal))
                {
                    continue;
                }

                var array = baseField["m_TableData.Array"];

                var result = new Dictionary<long, string>();

                foreach (var entry in array.Children)
                {
                    var id = entry["m_Id"].AsLong;
                    var value = entry["m_Localized"].AsString;

                    result[id] = value;
                }

                return result;
            }

            throw new InvalidDataException($"Could not find string table '{tableName}' in {bundlePath}");
        }
        finally
        {
            manager.UnloadAll();
        }
    }

    public static void WriteStringTable(string sourceBundlePath, string outputBundlePath, string tableName, IDictionary<long, string> replacements)
    {
        var manager = new AssetsManager();
        var bundle = manager.LoadBundleFile(sourceBundlePath, true);

        try
        {
            var assetsInstance =
                manager.LoadAssetsFileFromBundle(bundle, 0, false);

            var assetsFile = assetsInstance.file;

            var foundTable = false;

            foreach (var info in assetsFile
                         .GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var baseField = manager.GetBaseField(assetsInstance, info);

                if (!string.Equals(
                        baseField["m_Name"].AsString,
                        tableName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                foundTable = true;

                var tableData = baseField["m_TableData.Array"];
                var seenIds = new HashSet<long>();

                foreach (var entry in tableData.Children)
                {
                    var id = entry["m_Id"].AsLong;

                    if (!replacements.TryGetValue(id, out var value))
                    {
                        throw new InvalidDataException(
                            $"Missing localization entry id {id} in translation CSV.");
                    }

                    entry["m_Localized"].AsString = value;
                    seenIds.Add(id);
                }

                var extraIds = replacements.Keys
                    .Where(id => !seenIds.Contains(id))
                    .ToList();

                if (extraIds.Count > 0)
                {
                    throw new InvalidDataException(
                        "Translation CSV contains localization ids not present in the English table: " +
                        string.Join(", ", extraIds));
                }

                info.SetNewData(baseField);

                break;
            }

            if (!foundTable)
            {
                throw new InvalidDataException(
                    $"Could not find string table '{tableName}' in {sourceBundlePath}");
            }

            ReplaceAssetsFileInBundle(bundle, assetsFile);

            WriteBundle(bundle, outputBundlePath);
        }
        finally
        {
            manager.UnloadAll();
        }
    }

    /// <summary>
    /// 将修改后的 AssetsFile 设置回 UnityFS bundle 的第一个文件条目。
    /// 本游戏当前分析到的相关 bundle 中，第 0 个条目就是需要修改的 serialized AssetsFile。
    /// </summary>
    private static void ReplaceAssetsFileInBundle(
        BundleFileInstance bundle,
        AssetsFile assetsFile)
    {
        var directoryInfos = bundle.file.BlockAndDirInfo.DirectoryInfos;

        if (directoryInfos.Count == 0)
        {
            throw new InvalidDataException(
                "Bundle contains no directory entries.");
        }

        var directoryInfo = directoryInfos[0];

        if (!directoryInfo.IsSerialized)
        {
            throw new InvalidDataException(
                $"First bundle entry '{directoryInfo.Name}' is not a serialized AssetsFile.");
        }

        directoryInfo.SetNewData(assetsFile);
    }

    /// <summary>
    /// 写出修改后的 UnityFS bundle。
    /// 输出是合法的未压缩 UnityFS bundle。Unity 可以直接读取。
    /// </summary>
    private static void WriteBundle(BundleFileInstance bundle, string outputBundlePath)
    {
        var directory =
            Path.GetDirectoryName(outputBundlePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputBundlePath))
        {
            File.Delete(outputBundlePath);
        }

        using var writer = new AssetsFileWriter(outputBundlePath);

        bundle.file.Write(writer);
    }

}