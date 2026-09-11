using System.Text;

namespace ModemAPI
{
    public class IniFileResolver
    {
        /// <summary>
        /// Parse INI text into a map of sections to key/value pairs.
        /// - Sections are declared as [SectionName]
        /// - Keys are in form key = value
        /// - Comments start with ';' and are stripped (inline and full-line)
        /// - Empty lines are ignored
        /// - Keys before any section header go to a special section named "global"
        /// If duplicate keys occur within a section, the last one wins.
        /// </summary>
        public static Dictionary<string, Dictionary<string, string>> Parse(string iniText)
        {
            if (iniText == null)
            {
                throw new ArgumentNullException(nameof(iniText));
            }

            Dictionary<string, Dictionary<string, string>> sectionNameToEntries = new Dictionary<string, Dictionary<string, string>>();
            string currentSectionName = "global";
            sectionNameToEntries[currentSectionName] = new Dictionary<string, string>();

            using (StringReader reader = new StringReader(iniText))
            {
                string? rawLine;
                while ((rawLine = reader.ReadLine()) != null)
                {
                    string lineWithoutComments = StripComment(rawLine);
                    string line = lineWithoutComments.Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (IsSectionHeader(line))
                    {
                        string sectionName = ExtractSectionName(line);
                        if (!sectionNameToEntries.ContainsKey(sectionName))
                        {
                            sectionNameToEntries[sectionName] = new Dictionary<string, string>();
                        }
                        currentSectionName = sectionName;
                        continue;
                    }

                    int equalsIndex = line.IndexOf('=');
                    if (equalsIndex <= 0)
                    {
                        // Not a valid key=value pair; skip silently
                        continue;
                    }

                    string key = line.Substring(0, equalsIndex).Trim();
                    string value = line.Substring(equalsIndex + 1).Trim();

                    if (key.Length == 0)
                    {
                        continue;
                    }

                    sectionNameToEntries[currentSectionName][key] = value;
                }
            }

            return sectionNameToEntries;
        }

        /// <summary>
        /// Parse INI file from disk with specified encoding (UTF8 by default).
        /// </summary>
        public static Dictionary<string, Dictionary<string, string>> ParseFile(string filePath, Encoding? encoding = null)
        {
            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("INI file was not found", filePath);
            }

            Encoding useEncoding = encoding ?? Encoding.UTF8;
            string contents = File.ReadAllText(filePath, useEncoding);
            return Parse(contents);
        }

        private static bool IsSectionHeader(string line)
        {
            return line.Length >= 3 && line[0] == '[' && line[line.Length - 1] == ']';
        }

        private static string ExtractSectionName(string line)
        {
            // assumes IsSectionHeader(line) is true
            return line.Substring(1, line.Length - 2).Trim();
        }

        private static string StripComment(string line)
        {
            int semicolonIndex = line.IndexOf(';');
            if (semicolonIndex >= 0)
            {
                return line.Substring(0, semicolonIndex);
            }
            return line;
        }
    }
}
