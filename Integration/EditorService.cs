using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Raskladka.Integration
{
    public sealed class EditConversion
    {
        public string Replacement { get; private set; }
        public string Direction { get; private set; }
        public string Message { get; private set; }
        public EditConversion(string replacement, string direction, string message)
        { Replacement = replacement; Direction = direction; Message = message; }
    }

    public sealed class EditorResult
    {
        public bool Success { get; private set; }
        public string Direction { get; private set; }
        public string Message { get; private set; }
        internal EditorResult(bool success, string direction, string message)
        { Success = success; Direction = direction; Message = message; }
        internal static EditorResult Refuse(string message) { return new EditorResult(false, "", message); }
        internal static EditorResult Done(EditConversion change) { return new EditorResult(true, change.Direction, change.Message); }
    }

    public sealed class EditorService : IDisposable
    {
        internal const string SelectHint = "Поставьте курсор в слово или выделите текст";
        internal const string Unavailable = "В этом поле исправление недоступно";
        internal const string ContextChanged = "Поле или выделение изменилось";
        private readonly BrowserBridge browsers = new BrowserBridge();
        private readonly SemaphoreSlim queue = new SemaphoreSlim(1);
        private volatile bool disposed;

        public async Task<EditorResult> CorrectAsync(Func<string, EditConversion> convert, bool switchLayout)
        {
            try {
                NativeField field = NativeField.Capture();
                if (field == null || disposed) return EditorResult.Refuse(Unavailable);
                // Pin the numeric selection when WM_HOTKEY arrives. A queued request must
                // never borrow the next selection made in the same native editor.
                EditSpan? captured = field.Current() && field.Editable() ? (EditSpan?)field.Selection() : null;
                await queue.WaitAsync().ConfigureAwait(false);
                try {
                    if (disposed || !field.Current()) return EditorResult.Refuse(ContextChanged);
                    EditorResult result = await Task.Run(() => field.Browser
                        ? browsers.Correct(field.Current, convert)
                        : CorrectNative(field, captured, convert)).ConfigureAwait(false);
                    if (switchLayout && result.Success && !disposed) field.SwitchLayout(result.Direction);
                    return result;
                } finally { queue.Release(); }
            } catch (Exception) { return EditorResult.Refuse(Unavailable); }
        }

        private static TextPatternRange Selected(AutomationElement element)
        {
            object pattern;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return null;
            var ranges = ((TextPattern)pattern).GetSelection();
            return ranges.Length == 1 ? ranges[0] : null;
        }
        private static bool SafeElement(NativeField field, AutomationElement element)
        {
            if (!field.Editable() || element == null || element.Current.IsPassword || !element.Current.IsEnabled ||
                element.Current.NativeWindowHandle != field.Control.ToInt64()) return false;
            object valuePattern;
            return !element.TryGetCurrentPattern(ValuePattern.Pattern, out valuePattern) || !((ValuePattern)valuePattern).Current.IsReadOnly;
        }
        private static bool SameRange(TextPatternRange a, TextPatternRange b)
        {
            return a != null && b != null && a.CompareEndpoints(TextPatternRangeEndpoint.Start, b, TextPatternRangeEndpoint.Start) == 0
                && a.CompareEndpoints(TextPatternRangeEndpoint.End, b, TextPatternRangeEndpoint.End) == 0;
        }
        private EditorResult CorrectNative(NativeField field, EditSpan? captured, Func<string, EditConversion> convert)
        {
            if (!field.Current() || !field.Editable()) return EditorResult.Refuse(Unavailable);
            AutomationElement element = AutomationElement.FromHandle(field.Control);
            return CorrectSelection(field, element, captured, convert, delegate {
                return !disposed && field.Current() && Automation.Compare(element, AutomationElement.FocusedElement);
            });
        }

        private static bool TryWord(NativeField field, TextPatternRange caret, int nativeCaret, out EditSpan target, out string word)
        {
            target = new EditSpan(nativeCaret, nativeCaret);
            word = null;
            string text;
            int first, start, end;
            bool wrapsLeft, wrapsRight;
            if (field.TryCaretLine(nativeCaret, out text, out first, out wrapsLeft, out wrapsRight)) {
                if (CaretWord.TryFind(text, nativeCaret - first, out start, out end)) {
                    if ((!wrapsLeft || start > 1) && (!wrapsRight || end < text.Length - 1)) {
                        target = new EditSpan(first + start, first + end);
                        word = text.Substring(start, end - start);
                        return true;
                    }
                } else if (!wrapsLeft && !wrapsRight) return false;
            }

            // A soft wrap is not a word or paragraph boundary. Ask the provider for
            // bounded surrounding text when the displayed line cannot resolve the word.
            // Calculate native offsets only within the same logical line as the caret.
            for (int radius = 32; radius <= 1024; radius *= 2) {
                var left = caret.Clone();
                var right = caret.Clone();
                int leftMoved = left.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -radius);
                int rightMoved = right.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, radius);
                string prefix = left.GetText(4097), suffix = right.GetText(4097);
                if (prefix.Length > 4096 || suffix.Length > 4096) return false;
                text = prefix + suffix;
                if (!CaretWord.TryFind(text, prefix.Length, out start, out end)) {
                    if (leftMoved != -radius || prefix.IndexOfAny(new[] { '\r', '\n' }) >= 0) return false;
                    continue;
                }
                if ((leftMoved == -radius && start <= 1) || (rightMoved == radius && end >= text.Length - 1)) continue;
                long from = (long)nativeCaret + start - prefix.Length, to = (long)nativeCaret + end - prefix.Length;
                if (from < 0 || to > int.MaxValue) return false;
                target = new EditSpan((int)from, (int)to);
                word = text.Substring(start, end - start);
                return true;
            }
            return false;
        }

        private static bool ReadOnly(TextPatternRange range)
        {
            object attribute = range.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            return (attribute is bool && (bool)attribute) || attribute == TextPattern.MixedAttributeValue;
        }

        internal static EditorResult CorrectSelection(NativeField field, AutomationElement element, EditSpan? captured,
            Func<string, EditConversion> convert, Func<bool> focused)
        {
            Func<bool> canEdit = () => focused() && SafeElement(field, element);
            if (!canEdit()) return EditorResult.Refuse(Unavailable);
            EditSpan original = field.Selection(), target = original;
            if (!captured.HasValue || !captured.Value.Same(original)) return EditorResult.Refuse(ContextChanged);
            var current = Selected(element);
            if (current == null) return EditorResult.Refuse(Unavailable);
            bool collapsed = current.CompareEndpoints(TextPatternRangeEndpoint.Start, current, TextPatternRangeEndpoint.End) == 0;
            if (collapsed != original.Empty) return EditorResult.Refuse(ContextChanged);
            string source;
            if (collapsed) {
                if (!TryWord(field, current, original.Start, out target, out source)) return EditorResult.Refuse(SelectHint);
            } else {
                if (ReadOnly(current)) return EditorResult.Refuse(Unavailable);
                source = current.GetText(-1);
            }
            if (string.IsNullOrEmpty(source) || source.IndexOf('\0') >= 0) return EditorResult.Refuse(SelectHint);
            var change = convert(source);
            if (change == null || change.Replacement == null || change.Replacement == source)
                return EditorResult.Refuse(change == null ? Unavailable : change.Message);
            if (source.Length != change.Replacement.Length) return EditorResult.Refuse(Unavailable);
            if (!canEdit() || !original.Same(field.Selection()) || !SameRange(current, Selected(element))) return EditorResult.Refuse(ContextChanged);
            if (collapsed) {
                EditSpan latest;
                string word;
                if (!TryWord(field, current, original.Start, out latest, out word) || !latest.Same(target) || source != word ||
                    !focused() || !original.Same(field.Selection())) return EditorResult.Refuse(ContextChanged);
                field.SetSelection(target);
            }

            EditSpan finalCaret = collapsed ? original : new EditSpan(target.End, target.End);
            bool written = false;
            try {
                // Confirm the actual range after EM_SETSEL with an independent UIA
                // read. Providers can expose line breaks or characters differently.
                var selected = Selected(element);
                if (selected == null || !canEdit() || !target.Same(field.Selection()) || ReadOnly(selected) ||
                    selected.GetText(source.Length + 1) != source) return EditorResult.Refuse(ContextChanged);
                if (field.Rich) field.Query(0x0458); // EM_STOPGROUPTYPING
                if (!focused() || !target.Same(field.Selection())) return EditorResult.Refuse(ContextChanged);
                field.ReplaceSelection(change.Replacement);
                written = true;
                if (!focused() || !field.Selection().Same(new EditSpan(target.End, target.End))) return EditorResult.Refuse(ContextChanged);
                // Read back precisely the changed interval. Reusing native offsets
                // avoids expensive per-character UIA moves and preserves one undo unit.
                field.SetSelection(target);
                var check = Selected(element);
                if (check == null || !target.Same(field.Selection()) || check.GetText(source.Length + 1) != change.Replacement)
                    return EditorResult.Refuse(Unavailable);
            } finally {
                if ((collapsed || written) && focused() && target.Same(field.Selection()))
                    field.SetSelection(written ? finalCaret : original);
            }
            if (!focused() || !finalCaret.Same(field.Selection())) return EditorResult.Refuse(ContextChanged);
            return EditorResult.Done(change);
        }
        public void Dispose() { disposed = true; browsers.Dispose(); }
    }
}
