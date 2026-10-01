using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceOfCardsLocalizationTool
{
    internal static class TranslationFileService
    {
        internal static readonly string[] titles = ["Key", "English", "Japanese", "Target"];

        public static void Write(string path, IList<TranslationRow> rows)
        {
            var csv = new List<IList<string>> { titles };
            foreach (var row in rows)
                csv.Add([row.Key, row.English, row.Japanese, row.Target]);
            CsvCodec.Write(path, csv);
        }

        public static List<TranslationRow> Read(string path)
        {
            var csv = CsvCodec.Read(path);
            if (csv.Count == 0)
                throw new InvalidDataException("Empty CSV: " + path);

            var header = csv[0];
            if (header.Count < 4 || header[0] != "Key" || header[1] != "English" || header[2] != "Japanese" || header[3] != "Target")
                throw new InvalidDataException("CSV header must be: Key,English,Japanese,Target: " + path);

            var rows = new List<TranslationRow>();
            for (int i = 1; i < csv.Count; i++)
            {
                var cols = csv[i];
                if (cols.Count == 1 && cols[0].Length == 0)
                    continue;
                if (cols.Count != 4)
                    throw new InvalidDataException(string.Format("{0}: logical row {1} has {2} columns; expected 4.", path, i + 1, cols.Count));

                rows.Add(new TranslationRow
                {
                    Key = cols[0],
                    English = cols[1],
                    Japanese = cols[2],
                    Target = cols[3]
                });
            }
            return rows;
        }

        public static void ValidateKeys(string csvPath, IList<TranslationRow> rows, IEnumerable<string> expectedKeys)
        {
            var expected = expectedKeys.ToList();
            var actual = rows.Select(r => r.Key).ToList();

            var duplicates = actual.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
                throw new InvalidDataException(csvPath + " contains duplicate keys: " + string.Join(", ", duplicates.Take(10)));

            var expectedSet = new HashSet<string>(expected);
            var actualSet = new HashSet<string>(actual);
            var missing = expectedSet.Except(actualSet).Take(10).ToList();
            var extra = actualSet.Except(expectedSet).Take(10).ToList();
            if (missing.Count > 0 || extra.Count > 0 || actual.Count != expected.Count)
            {
                throw new InvalidDataException(string.Format(
                    "{0} key set does not match the original resource. Missing: [{1}] Extra: [{2}]",
                    csvPath, string.Join(", ", missing), string.Join(", ", extra)));
            }
        }
    }
}
