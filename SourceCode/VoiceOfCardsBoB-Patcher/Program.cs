using System.Diagnostics;
using System.Text;

namespace VoiceOfCardsPatch
{
    internal static class Program
    {
        private const string GameExeName = "VoiceofCardsTheBeastsofBurden.exe";

        private static readonly PatchEntry[] PatchEntries =
        [
            new(@"asset_text_jp", @"Patch.asset_text_jp", @"VoiceofCardsTheBeastsofBurden_Data\StreamingAssets\Windows\asset_text_jp"),

            new(@"localization-string-tables-japanese(ja)_assets_all.bundle",
                @"Patch.localization-string-tables-japanese(ja)_assets_all.bundle",
                @"VoiceofCardsTheBeastsofBurden_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-japanese(ja)_assets_all.bundle"),

            new(@"sharedassets0.assets", @"Patch.sharedassets0.assets", @"VoiceofCardsTheBeastsofBurden_Data\sharedassets0.assets"),

            new(@"sharedassets0.assets.resS", @"Patch.sharedassets0.assets.resS", @"VoiceofCardsTheBeastsofBurden_Data\sharedassets0.assets.resS")
        ];

        internal enum ExitCode
        {
            Success = 0,
            PatchFailed = 1,
            GameNotFound = 2,
            GameRunning = 3,
            WriteAccessFailed = 4
        }

        private static int Main(string[] args) => (int)Run(args);

        private static ExitCode Run(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
            Console.Title = "Voice of Cards: The Beasts of Burden 汉化补丁";

            string gameRoot = Path.GetFullPath(AppContext.BaseDirectory);
            string gameExe = Path.Combine(gameRoot, GameExeName);

            Console.WriteLine("Voice of Cards: The Beasts of Burden 汉化补丁");
            Console.WriteLine("游戏目录：" + gameRoot);
            Console.WriteLine();

            if (!File.Exists(gameExe))
            {
                Console.Error.WriteLine("未在补丁程序同级找到：" + GameExeName);
                Console.Error.WriteLine("请把补丁 EXE 放到游戏根目录后再运行。");
                Pause();
                return ExitCode.GameNotFound;
            }

            var patcher = new Patcher(gameRoot, PatchEntries);
            PatchAction? requestedAction = ParseAction(args);
            if (requestedAction is not null)
                return Execute(patcher, requestedAction.Value, gameRoot, false);

            while (true)
            {
                Console.WriteLine("1. 应用汉化补丁（覆盖日语）");
                Console.WriteLine("2. 复原原始文件");
                Console.WriteLine("0. 退出");
                Console.Write("请选择：");

                string? input = Console.ReadLine()?.Trim();
                Console.WriteLine();
                switch (input)
                {
                    case "1":
                        return Execute(patcher, PatchAction.Apply, gameRoot, true);
                    case "2":
                        return Execute(patcher, PatchAction.Restore, gameRoot, true);
                    case "0":
                        return 0;
                    default:
                        Console.WriteLine("请输入 1、2 或 0。");
                        Console.WriteLine();
                        break;
                }
            }
        }

        private static ExitCode Execute(Patcher patcher, PatchAction action, string gameRoot, bool interactive)
        {
            if (IsGameRunning())
            {
                Console.Error.WriteLine("检测到游戏正在运行。请先完全退出游戏，再执行补丁操作。");
                if (interactive)
                    Pause();
                return ExitCode.GameRunning;
            }

            if (!patcher.CanWriteTargetDirectories(action))
            {
                if (ElevationHelper.IsAdmin())
                {
                    Console.Error.WriteLine("即使使用管理员权限，目标目录仍不可写。请检查文件/目录权限。");
                    if (interactive)
                        Pause();
                    return ExitCode.WriteAccessFailed;
                }

                Console.WriteLine("当前无法写入目标目录，将尝试使用管理员权限重新运行。");
                Console.WriteLine("即将弹出 Windows UAC 确认窗口。");

                string argument = action == PatchAction.Apply ? "--apply" : "--restore";
                bool started = ElevationHelper.RestartElevated(argument, gameRoot);

                if (interactive && !started)
                    Pause();

                return started ? ExitCode.Success : ExitCode.WriteAccessFailed;
            }

            bool success = action == PatchAction.Apply ? patcher.Apply() : patcher.Restore();

            if (success && action == PatchAction.Apply)
                Console.WriteLine("请在游戏内将文本语言设置为日语以显示中文。");

            if (interactive)
                Pause();

            return success ? ExitCode.Success : ExitCode.PatchFailed;
        }

        private static PatchAction? ParseAction(string[] args)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--apply", StringComparison.OrdinalIgnoreCase))
                    return PatchAction.Apply;
                if (string.Equals(arg, "--restore", StringComparison.OrdinalIgnoreCase))
                    return PatchAction.Restore;
            }
            return null;
        }

        private static void Pause()
        {
            Console.WriteLine();
            Console.Write("按 Enter 键退出……");
            Console.ReadLine();
        }

        private static bool IsGameRunning()
        {
            string processName = Path.GetFileNameWithoutExtension(GameExeName);
            return Process.GetProcessesByName(processName).Length > 0;
        }
    }
}

