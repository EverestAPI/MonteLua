using System.Collections.Generic;

namespace NLua.Extensions {
    static class StringExtensions {
        /// <summary>
        /// Split a string, skipping the separator when prefaced with the given escape character.
        /// </summary>
        public static IEnumerable<string> SplitWithEscape(this string input, char separator, char escapeCharacter) {
            int start = 0;
            int index = 0;

            while (index < input.Length) {
                index = input.IndexOf(separator, index);
                if (index == -1)
                    break;

                if (input[index - 1] == escapeCharacter) {
                    input = input.Remove(index - 1, 1);
                    continue;
                }

                yield return input.Substring(start, index - start);
                index++;
                start = index;
            }

            yield return input.Substring(start);
        }
    }
}
