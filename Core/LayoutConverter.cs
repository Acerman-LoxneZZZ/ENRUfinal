using System;
using System.Collections.Generic;

namespace Raskladka
{
    public enum ConversionMode { Auto, EnToRu, RuToEn }

    public sealed class ConversionResult
    {
        public string Text { get; private set; }
        public bool Changed { get; private set; }
        public ConversionMode Direction { get; private set; }
        public string Message { get; private set; }

        internal ConversionResult(string text, bool changed, ConversionMode direction, string message)
        {
            Text = text;
            Changed = changed;
            Direction = direction;
            Message = message;
        }
    }

    public static class LayoutConverter
    {
        private static readonly Dictionary<char, char> EnglishKeys = new Dictionary<char, char>();
        private static readonly Dictionary<char, char> RussianKeys = new Dictionary<char, char>();

        static LayoutConverter()
        {
            // Windows US (00000409) / Russian (00000419), matching physical rows.
            AddRow("`1234567890-=", "ё1234567890-=");
            AddRow("~!@#$%^&*()_+", "Ё!\"№;%:?*()_+");
            AddRow("qwertyuiop[]\\", "йцукенгшщзхъ\\");
            AddRow("QWERTYUIOP{}|", "ЙЦУКЕНГШЩЗХЪ/");
            AddRow("asdfghjkl;'", "фывапролджэ");
            AddRow("ASDFGHJKL:\"", "ФЫВАПРОЛДЖЭ");
            AddRow("zxcvbnm,./", "ячсмитьбю.");
            AddRow("ZXCVBNM<>?", "ЯЧСМИТЬБЮ,");
            // The extra ISO key duplicates the backslash key. OEM_5 (above Enter)
            // is canonical for both '\\' and shifted '|', producing '\\' and '/'.
        }

        private static void AddRow(string english, string russian)
        {
            for (int i = 0; i < english.Length; i++)
            {
                EnglishKeys.Add(english[i], russian[i]);
                RussianKeys.Add(russian[i], english[i]);
            }
        }

        public static ConversionResult Convert(string text, ConversionMode mode)
        {
            if (string.IsNullOrEmpty(text))
                return new ConversionResult(text, false, mode, "Выделите текст и нажмите сочетание ещё раз");

            if (mode == ConversionMode.Auto)
            {
                bool english = false;
                bool russian = false;
                foreach (char c in text)
                {
                    english |= (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                    russian |= (c >= 'А' && c <= 'я') || c == 'ё' || c == 'Ё';
                    if (english && russian)
                        return new ConversionResult(text, false, mode, "Выделите фрагмент на одной раскладке");
                }
                if (!english && !russian)
                    return new ConversionResult(text, false, mode, "Не удалось определить раскладку");
                mode = english ? ConversionMode.EnToRu : ConversionMode.RuToEn;
            }

            if (mode != ConversionMode.EnToRu && mode != ConversionMode.RuToEn)
                throw new ArgumentOutOfRangeException("mode");

            var keys = mode == ConversionMode.EnToRu ? EnglishKeys : RussianKeys;
            char[] output = null;
            for (int i = 0; i < text.Length; i++)
            {
                char mapped;
                if (keys.TryGetValue(text[i], out mapped) && mapped != text[i])
                {
                    if (output == null) output = text.ToCharArray();
                    output[i] = mapped;
                }
            }
            if (output == null) return new ConversionResult(text, false, mode, "Текст уже на нужной раскладке");
            string label = mode == ConversionMode.EnToRu ? "Исправлено · EN → RU" : "Исправлено · RU → EN";
            return new ConversionResult(new string(output), true, mode, label);
        }
    }
}
