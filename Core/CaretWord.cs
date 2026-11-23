using System.Globalization;

namespace Raskladka
{
    internal static class CaretWord
    {
        // Half-open UTF-16 offsets, matching EDIT selections and browser selectionStart/End.
        internal static bool TryFind(string text, int caret, out int start, out int end)
        {
            start = end = caret;
            if (text == null || caret < 0 || caret > text.Length ||
                (caret > 0 && caret < text.Length && char.IsSurrogatePair(text, caret - 1))) return false;

            int anchor = caret;
            while (!InWord(text, anchor))
            {
                if (anchor == 0) return false;
                anchor = Previous(text, anchor);
                if (!InWord(text, anchor) && !CanPass(text, anchor)) return false;
            }
            start = anchor;
            while (start > 0 && InWord(text, Previous(text, start))) start = Previous(text, start);
            end = Next(text, anchor);
            while (InWord(text, end)) end = Next(text, end);
            return true;
        }

        private static int Previous(string text, int index)
        {
            return index >= 2 && char.IsSurrogatePair(text, index - 2) ? index - 2 : index - 1;
        }

        private static int Next(string text, int index)
        {
            return index + (char.IsSurrogatePair(text, index) ? 2 : 1);
        }

        private static bool CoreCharacter(string text, int index)
        {
            if (index < 0 || index >= text.Length) return false;
            UnicodeCategory kind = CharUnicodeInfo.GetUnicodeCategory(text, index);
            return char.IsLetter(text, index) || kind == UnicodeCategory.DecimalDigitNumber || text[index] == '_' ||
                kind == UnicodeCategory.NonSpacingMark || kind == UnicodeCategory.SpacingCombiningMark ||
                kind == UnicodeCategory.EnclosingMark;
        }

        private static bool InWord(string text, int index)
        {
            if (CoreCharacter(text, index)) return true;
            if (index <= 0 || index >= text.Length) return false;
            char c = text[index];
            if (c == '-' || c == '\'' || c == '’')
                return CoreCharacter(text, Previous(text, index)) && CoreCharacter(text, Next(text, index));
            // On a US keyboard these keys can be letters inside mistyped Russian
            // words ("lj,hsq" → "добрый"). A comma between Cyrillic words is punctuation.
            const string letterKeys = ".,;[]`~<>:{}\"";
            return index + 1 < text.Length && letterKeys.IndexOf(c) >= 0
                && AsciiLetter(text[index - 1]) && AsciiLetter(text[index + 1]);
        }

        private static bool AsciiLetter(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
        }

        private static bool CanPass(string text, int index)
        {
            return text[index] == '\t' || CharUnicodeInfo.GetUnicodeCategory(text, index) == UnicodeCategory.SpaceSeparator
                || char.IsPunctuation(text, index);
        }
    }
}
