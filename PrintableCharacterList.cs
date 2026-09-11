namespace ModemAPI
{
    /// <summary>
    /// Checks if it is allowed to transmit this set of characters.
    /// </summary>
    public class PrintableCharacterList
    {
        /// <summary>
        /// Standart Printable Characters (used for Metadata).
        /// </summary>
        public static readonly char[] PrintableCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVXYZ+-/=^!@#$%&*()[]{}<>0123456789.:;\"'~`?_\\|\r\n".ToCharArray();
        /// <summary>
        /// Limited Printable Characters (used for Query Type).
        /// </summary>
        public static readonly char[] PrintableCharactersQueryType = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:=~?!._-".ToCharArray();
        /// <summary>
        /// Verifies the character for being illegal in Standart List.
        /// </summary>
        /// <param name="c">Character for verifying</param>
        /// <returns>true if the character illegal, otherwise - false</returns>
        public static bool IsCharacherWrong(char c)
        {
            return !PrintableCharacters.Contains(c);
        }
        public static bool IsCharacterWrongQueryType(char c)
        {
            return !PrintableCharactersQueryType.Contains(c);
        }
        public static bool IsStringWrong(string s, bool isLimitedSet)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (!(isLimitedSet ? PrintableCharactersQueryType : PrintableCharacters).Contains(s[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
