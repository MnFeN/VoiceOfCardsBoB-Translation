using System.Reflection;

namespace VoiceOfCardsPatch
{
    internal enum PatchAction
    {
        Apply,
        Restore
    }

    internal sealed class Patcher(string gameRoot, IReadOnlyList<PatchEntry> entries)
    {
        private const string BackupSuffix = ".backup";
        private readonly string gameRoot = gameRoot;
        private readonly IReadOnlyList<PatchEntry> entries = entries;
        public bool CanWriteTargetDirectories(PatchAction action)
        {
            IEnumerable<PatchEntry> relevantEntries = action == PatchAction.Apply
                ? entries                                               // Apply 时检查所有补丁目标目录
                : entries.Where(e => File.Exists(GetBackupPath(e)));    // Restore 时只检查那些确实存在 backup 的文件所在目录

            var directories = relevantEntries
                .Select(e => Path.GetDirectoryName(GetTargetPath(e))!)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory))
                    continue;

                if (!CanWriteDirectory(directory))
                    return false;
            }

            return true;
        }

        private static bool CanWriteDirectory(string directory)
        {
            string probe = Path.Combine(directory, $".voc_patch_write_test_{Guid.NewGuid():N}.tmp");

            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(probe))
                        File.Delete(probe);
                }
                catch
                {
                }
            }
        }

        public bool Apply()
        {
            Console.WriteLine();
            Console.WriteLine("开始应用补丁……");

            int completed = 0;
            int failed = 0;

            foreach (PatchEntry entry in entries)
            {
                string target = GetTargetPath(entry);
                string backup = GetBackupPath(entry);

                try
                {
                    if (!File.Exists(backup))
                    {
                        File.Copy(target, backup);
                        Console.WriteLine("  [已备份] " + entry.RelativePath);
                    }

                    ExtractResource(entry, target);
                    Console.WriteLine("  [已替换] " + entry.RelativePath);
                    completed++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("  [失败] " + entry.RelativePath + "：" + ex.Message);
                    failed++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"应用结果：成功 {completed}，失败 {failed}。");
            return failed == 0;
        }

        public bool Restore()
        {
            Console.WriteLine();
            Console.WriteLine("开始复原……");

            int restored = 0;
            int missing = 0;
            int failed = 0;

            foreach (PatchEntry entry in entries)
            {
                string target = GetTargetPath(entry);
                string backup = GetBackupPath(entry);

                if (!File.Exists(backup))
                {
                    Console.Error.WriteLine("  [缺少备份] " + entry.RelativePath + BackupSuffix);
                    missing++;
                    continue;
                }

                try
                {
                    if (File.Exists(target))
                        File.Delete(target);

                    File.Move(backup, target);
                    Console.WriteLine("  [已复原] " + entry.RelativePath);
                    restored++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("  [失败] " + entry.RelativePath + "：" + ex.Message);
                    failed++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"复原结果：成功 {restored}，缺少备份 {missing}，失败 {failed}。");
            return failed == 0 && missing == 0;
        }

        private static void ExtractResource(PatchEntry entry, string destination)
        {
            using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(entry.ResourceName) 
                ?? throw new InvalidDataException("EXE 内部找不到补丁资源：" + entry.ResourceName);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            resource.CopyTo(output);
        }

        private string GetTargetPath(PatchEntry entry)
        {
            string relative = entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(gameRoot, relative));
        }

        private string GetBackupPath(PatchEntry entry)
        {
            return GetTargetPath(entry) + BackupSuffix;
        }
    }
}