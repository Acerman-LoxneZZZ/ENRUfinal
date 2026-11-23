(() => {
  let port;
  let listeners = null;
  let before = null;
  let series = null;
  let request = null;
  let expiry;
  const reset = () => { before = null; series = null; };

  function writable(field) {
    if (!(field instanceof HTMLTextAreaElement ||
      (field instanceof HTMLInputElement && ["text", "search", "url", "tel"].includes(field.type)))) return false;
    if (field.disabled || field.readOnly || field.matches(":disabled") ||
      field.closest('[inert], [aria-readonly="true"], [aria-disabled="true"]')) return false;
    if (/(?:password|one-time-code|cc-)/i.test(field.autocomplete)) return false;
    return getComputedStyle(field).webkitTextSecurity === "none";
  }
  function focused(field) {
    return document.hasFocus() && document.visibilityState === "visible" && field === document.activeElement && writable(field);
  }
  function matching(field, start, end) {
    return focused(field) && field.selectionStart === start && field.selectionEnd === end;
  }
  function cancelRequest() {
    clearTimeout(expiry); request = null;
  }
  function configure(enabled) {
    if (enabled === !!listeners) return;
    reset();
    if (!enabled) { listeners?.abort(); listeners = null; return; }
    listeners = new AbortController();
    const options = { capture: true, signal: listeners.signal };
    document.addEventListener("beforeinput", e => {
      const field = e.target;
      if (!e.isTrusted || e.isComposing || !focused(field) ||
          !["insertText", "deleteContentBackward"].includes(e.inputType) ||
          field.selectionStart !== field.selectionEnd) { reset(); return; }
      before = { field, type: e.inputType, at: field.selectionStart, time: performance.now() };
      if (series && (series.field !== field || series.end !== before.at || before.time - series.time > 8000)) series = null;
    }, options);
    document.addEventListener("input", e => {
      const edit = before;
      before = null;
      if (!edit || !e.isTrusted || edit.type !== e.inputType || edit.field !== e.target ||
          !focused(edit.field) || edit.field.selectionStart !== edit.field.selectionEnd) { reset(); return; }
      const end = edit.field.selectionEnd;
      if (edit.type === "insertText" && end > edit.at) {
        series = { field: edit.field, start: series ? series.start : edit.at, end, time: edit.time };
      } else if (edit.type === "deleteContentBackward" && series && end > series.start && end < edit.at) {
        series.end = end; series.time = edit.time;
      } else reset();
    }, options);
    document.addEventListener("selectionchange", () => {
      if (series && !matching(series.field, series.end, series.end)) reset();
    }, options);
    for (const event of ["focusin", "focusout", "submit", "paste", "cut", "drop", "pointerdown", "compositionstart"])
      document.addEventListener(event, reset, options);
    window.addEventListener("blur", reset, options);
  }

  // Chromium exposes the selected part of native inputs through window.getSelection().
  // A temporary selection lets us read only this interval, including for a recent edit.
  function read(field, start, end, a, b) {
    if (!matching(field, a, b)) return null;
    if (start !== a || end !== b) field.setSelectionRange(start, end);
    if (!matching(field, start, end)) return null;
    const selected = String(window.getSelection()).replace(/\r\n?/g, "\n");
    if ((start !== a || end !== b) && matching(field, start, end)) field.setSelectionRange(a, b);
    return (selected.length === end - start) ? selected : null;
  }

  function prepare(id) {
    cancelRequest();
    const field = document.activeElement;
    if (!document.hasFocus() || document.visibilityState !== "visible" || field instanceof HTMLIFrameElement) return { state: "inactive" };
    if (!writable(field)) return { state: "unsupported" };
    const a = field.selectionStart, b = field.selectionEnd;
    let start = a;
    if (a === b) {
      if (!listeners || !series || series.field !== field || series.end !== b || performance.now() - series.time > 8000)
        return { state: "empty" };
      start = series.start;
    }
    if (b - start > 120000) return { state: "unsupported" };
    const value = read(field, start, b, a, b);
    if (!value || !matching(field, a, b)) return { state: "unsupported" };
    request = { id, field, a, b, start, value };
    expiry = setTimeout(cancelRequest, 2000);
    return { state: "ready", value };
  }

  function commit(id, replacement) {
    const original = request;
    cancelRequest();
    if (!original || id !== original.id || typeof replacement !== "string") return "inactive";
    const {field, a, b, start, value} = original;
    if (replacement.length !== value.length || read(field, start, b, a, b) !== value || !matching(field, a, b)) return "inactive";
    if (a !== start) field.setSelectionRange(start, b);
    if (!matching(field, start, b)) return "inactive";
    // Chromium's editing command performs a single native undoable edit. Its return
    // value alone does not prove success: check the actual text and final caret.
    document.execCommand("insertText", false, replacement);
    reset();
    if (!matching(field, b, b) || read(field, start, b, b, b) !== replacement || !matching(field, b, b)) return "inactive";
    return "done";
  }
  function connect() {
    if (port) return;
    try { port = chrome.runtime.connect({ name: "field" }); } catch { return; }
    port.onDisconnect.addListener(() => { port = null; configure(false); cancelRequest(); });
    port.onMessage.addListener(message => {
      if (message.op === "config") { configure(message.enabled === true); return; }
      if (message.op === "release") { if (request?.id === message.id) cancelRequest(); return; }
      try {
        const reply = message.op === "prepare" ? prepare(message.id) : { state: commit(message.id, message.value) };
        port?.postMessage({ id: message.id, ...reply });
      } catch { port?.postMessage({ id: message.id, state: "unsupported" }); }
    });
  }
  // A service worker can be restarted by the browser after idling.
  window.addEventListener("focus", connect, true);
  window.addEventListener("pageshow", connect);
  window.addEventListener("blur", cancelRequest);
  document.addEventListener("focusout", cancelRequest, true);
  document.addEventListener("visibilitychange", () => { if (document.hidden) { reset(); cancelRequest(); } });
  connect();
})();
