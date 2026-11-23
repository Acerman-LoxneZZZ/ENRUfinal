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
        internal const string SelectHint = "Выделите текст и нажмите сочетание ещё раз";
        internal const string Unavailable = "В этом поле исправление недоступно";
        internal const string ContextChanged = "Поле или выделение изменилось";
        private readonly RecentNative recent = new RecentNative();
        private readonly BrowserBridge browsers = new BrowserBridge();
        private readonly SemaphoreSlim queue = new SemaphoreSlim(1);
        private volatile bool disposed;

        public void SetRecentEnabled(bool enabled) { recent.Enable(enabled); browsers.Enable(enabled); }
        public async Task<EditorResult> CorrectAsync(Func<string, EditConversion> convert, bool switchLayout)
        {
            try {
                NativeField field = NativeField.Capture();
                if (field == null || disposed) return EditorResult.Refuse(Unavailable);
                // Pin the numeric selection when WM_HOTKEY arrives. A queued request must
                // never borrow the next selection made in the same native editor.
                EditSpan? captured = field.NativeEdit && field.Editable() ? (EditSpan?)field.Selection() : null;
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
                element.Current.NativeWindowHandle != field.Control.ToInt64() ||
                !Automation.Compare(element, AutomationElement.FocusedElement)) return false;
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
            if (!field.Editable()) return EditorResult.Refuse(Unavailable);
            AutomationElement element = AutomationElement.FromHandle(field.Control);
            if (!SafeElement(field, element)) return EditorResult.Refuse(Unavailable);
            EditSpan original = field.Selection(), target = original;
            if (!captured.HasValue || !captured.Value.Same(original)) return EditorResult.Refuse(ContextChanged);
            if (original.Empty) {
                EditSpan? bound = recent.Take(field, original.End);
                if (!bound.HasValue) return EditorResult.Refuse(SelectHint);
                target = bound.Value;
            }
            TextPatternRange current = Selected(element);
            if (current == null) return EditorResult.Refuse(Unavailable);
            TextPatternRange range = current.Clone();
            if (original.Empty) {
                int distance = target.Start - target.End;
                if (range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, distance) != distance)
                    return EditorResult.Refuse(SelectHint);
            }
            object readOnly = range.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            if ((readOnly is bool && (bool)readOnly) || readOnly == TextPattern.MixedAttributeValue) return EditorResult.Refuse(Unavailable);
            string source = range.GetText(-1);
            if (String.IsNullOrEmpty(source) || source.IndexOf('\0') >= 0 ||
                (original.Empty && source.Length != target.End - target.Start)) return EditorResult.Refuse(SelectHint);
            EditConversion change = convert(source);
            if (change == null || change.Replacement == null || change.Replacement == source)
                return EditorResult.Refuse(change == null ? Unavailable : change.Message);
            if (disposed || !SafeElement(field, element) || !field.Selection().Same(original) || !SameRange(current, Selected(element)) ||
                range.GetText(-1) != source) return EditorResult.Refuse(ContextChanged);
            if (original.Empty) range.Select();
            if (disposed || !SafeElement(field, element) || !field.Selection().Same(target) || !SameRange(range, Selected(element)) ||
                range.GetText(-1) != source || !field.Current()) return EditorResult.Refuse(ContextChanged);
            if (field.Rich) field.Query(0x0458); // EM_STOPGROUPTYPING
            if (!field.Current() || !field.Selection().Same(target)) return EditorResult.Refuse(ContextChanged);
            field.ReplaceSelection(change.Replacement);
            recent.Reset();
            EditSpan after = field.Selection();
            if (!field.Current() || !after.Empty || after.End != target.End) return EditorResult.Refuse(ContextChanged);
            TextPatternRange check = Selected(element);
            if (check == null) return EditorResult.Refuse(Unavailable);
            // Maintain the starting text pointer even when the provider moves the selection
            // to the end. Convert preserves the physical character count and newlines.
            var verification = check.Clone();
            verification.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.Start);
            if (verification.GetText(-1) != change.Replacement) return EditorResult.Refuse(Unavailable);
            return EditorResult.Done(change);
        }
        public void Dispose() { disposed = true; recent.Dispose(); browsers.Dispose(); }
    }
}
