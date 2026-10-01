using System;
using System.IO;
using System.Text;

namespace VoiceOfCardsLocalizationTool
{
    internal static class Program
    {
        private static int Main(string[] _)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);

            string root = FindProjectRoot(AppContext.BaseDirectory);
            var service = new ProjectService(root);

            while (true)
            {
                Console.Clear();
                Console.WriteLine("Voice of Cards: The Beasts of Burden - Localization Tool");
                Console.WriteLine("English slot replacement workflow");
                Console.WriteLine();
                Console.WriteLine("1. Generate translation CSV files from OriginalGameFiles");
                Console.WriteLine("2. Generate GeneratedGameFiles resources from Translation CSV files");
                Console.WriteLine("0. Exit");
                Console.WriteLine();
                Console.Write("Select: ");

                string? input = Console.ReadLine();

                try
                {
                    Console.WriteLine();

                    switch (input)
                    {
                        case "1":
                            service.ExportTranslationCsv();
                            break;

                        case "2":
                            service.BuildOutput();
                            break;

                        case "0":
                            return 0;

                        default:
                            Console.WriteLine("Unknown option.");
                            Pause();
                            continue;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("ERROR");
                    Console.WriteLine(ex.Message);
                    Console.WriteLine();
                    Console.WriteLine(ex.ToString());
                }

                Pause();
            }
        }

        private static string FindProjectRoot(string start)
        {
            var dir = new DirectoryInfo(start);

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "OriginalGameFiles")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "Translation")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            string current = Directory.GetCurrentDirectory();

            if (Directory.Exists(Path.Combine(current, "OriginalGameFiles")))
                return current;

            throw new DirectoryNotFoundException("Could not locate the project root containing OriginalGameFiles and Translation.");
        }

        private static void Pause()
        {
            Console.WriteLine();
            Console.Write("Press Enter to continue...");
            Console.ReadLine();
        }
    }
}