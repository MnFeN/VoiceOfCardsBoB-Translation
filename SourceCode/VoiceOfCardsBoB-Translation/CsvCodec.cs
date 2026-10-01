using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VoiceOfCardsLocalizationTool
{
    /// <summary>
    /// Minimal RFC 4180-style CSV reader/writer.<br />
    /// Embedded CR/LF characters are stored as real newlines inside quoted fields.<br />
    /// Double quotes inside a field are escaped as two double quotes.
    /// </summary>
    internal static class CsvCodec
    {
        public static List<List<string>> Read(string path)
        {
            string text = File.ReadAllText(path, new UTF8Encoding(true));
            return Parse(text);
        }

        public static List<List<string>> Parse(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    continue;
                }

                if (c == '"' && field.Length == 0)
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;

                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                }
                else
                {
                    field.Append(c);
                }
            }

            if (quoted)
                throw new InvalidDataException("CSV ended inside a quoted field.");

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }

            return rows;
        }

        public static void Write(string path, IList<IList<string>> rows)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < rows[r].Count; c++)
                {
                    if (c != 0)
                        writer.Write(',');
                    writer.WriteField(rows[r][c] ?? string.Empty);
                }
                writer.Write("\r\n");
            }
        }

        public static string SerializeTwoColumn(IEnumerable<KeyValuePair<string, string>> rows)
        {
            var sb = new StringBuilder();
            foreach (var pair in rows)
            {
                sb.AppendField(pair.Key ?? string.Empty);
                sb.Append(',');
                sb.AppendField(pair.Value ?? string.Empty);
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        private static void WriteField(this TextWriter writer, string value)
        {
            writer.Write('"');
            writer.Write(value.Replace("\"", "\"\""));
            writer.Write('"');
        }

        private static void AppendField(this StringBuilder sb, string value)
        {
            sb.Append('"');
            sb.Append(value.Replace("\"", "\"\""));
            sb.Append('"');
        }
    }
}
