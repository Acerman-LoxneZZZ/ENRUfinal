(() => {
  let port;
  let request;
  let expiry;

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
    clearTimeout(expiry);
    request = null;
  }

  // Reading a native input selection keeps unrelated field contents out of the bridge.
  function read(field, start, end, a, b, allowShortEnd = false) {
    if (!matching(field, a, b)) return null;
    const temporary = start !== a || end !== b;
    const direction = field.selectionDirection;
    const left = field.scrollLeft, top = field.scrollTop;
    try {
      if (temporary) field.setSelectionRange(start, end);
      if (allowShortEnd) end = Math.min(end, field.selectionEnd);
      if (!matching(field, start, end)) return null;
      const text = String(window.getSelection()).replace(/\r\n?/g, "\n");
      return text.length === end - start ? text : null;
    } finally {
      if (temporary && matching(field, start, end)) {
        field.setSelectionRange(a, b, direction);
        field.scrollLeft = left;
        field.scrollTop = top;
      }
    }
  }

  // Same boundaries as Core/CaretWord.cs. These punctuation keys type Russian
  // letters on an English keyboard, e.g. the comma inside "lj,hsq" (добрый).
  const words = /[\p{L}\p{M}\p{Nd}_]+(?:(?:['’-]|(?<=[a-zA-Z])[.,;\[\]`~<>:{}"](?=[a-zA-Z]))[\p{L}\p{M}\p{Nd}_]+)*/gu;
  const separators = /^[\p{Zs}\p{P}\t]*$/u;
  function wordAt(text, caret) {
    if (/[\uD800-\uDBFF]/.test(text.charAt(caret - 1)) && /[\uDC00-\uDFFF]/.test(text.charAt(caret))) return null;
    let previous;
    for (const match of text.matchAll(words)) {
      const start = match.index, end = start + match[0].length;
      if (start > caret) break;
      if (caret <= end) return { start, end };
      previous = { start, end };
    }
    return previous && separators.test(text.slice(previous.end, caret)) ? previous : null;
  }
  function target(field, a, b) {
    if (a !== b) {
      if (b - a > 120000) return null;
      const value = read(field, a, b, a, b);
      return value ? { start: a, end: b, value } : null;
    }
    // Grow lazily for long words or runs of spaces. Leave enough context to
    // recognize connecting punctuation and surrogate pairs at both boundaries.
    for (let radius = 64; radius <= 65536; radius *= 4) {
      const from = Math.max(0, a - radius), to = a + radius;
      const text = read(field, from, to, a, b, true);
      if (text === null) return null;
      const range = wordAt(text, a - from);
      if (!range) {
        if (from && separators.test(text.slice(0, a - from))) continue;
        return null;
      }
      if ((from && range.start < 3) || (from + text.length === to && range.end > text.length - 3)) continue;
      return { start: from + range.start, end: from + range.end, value: text.slice(range.start, range.end) };
    }
    return null;
  }

  function prepare(id) {
    cancelRequest();
    const field = document.activeElement;
    if (!document.hasFocus() || document.visibilityState !== "visible" || field instanceof HTMLIFrameElement) return { state: "inactive" };
    if (!writable(field)) return { state: "unsupported" };
    const a = field.selectionStart, b = field.selectionEnd;
    if (!Number.isInteger(a) || !Number.isInteger(b)) return { state: "unsupported" };
    const found = target(field, a, b);
    if (!found || !matching(field, a, b)) return { state: a === b ? "empty" : "unsupported" };
    request = { id, field, a, b, ...found };
    expiry = setTimeout(cancelRequest, 2000);
    return { state: "ready", value: found.value };
  }

  function commit(id, replacement) {
    const original = request;
    if (!original || id !== original.id || typeof replacement !== "string") return "inactive";
    cancelRequest();
    const { field, a, b, start, end, value } = original;
    if (replacement.length !== value.length || !matching(field, a, b)) return "inactive";
    const now = target(field, a, b);
    if (!now || now.start !== start || now.end !== end || now.value !== value || !matching(field, a, b)) return "inactive";
    const implicit = a === b;
    if (implicit) field.setSelectionRange(start, end);
    if (!matching(field, start, end)) return "inactive";
    try {
      // Chromium records this replacement as one native undo entry.
      document.execCommand("insertText", false, replacement);
    } finally {
      if (implicit && matching(field, start, end)) field.setSelectionRange(a, a);
    }
    if (!matching(field, end, end) || read(field, start, end, end, end) !== replacement || !matching(field, end, end)) return "inactive";
    if (implicit) field.setSelectionRange(a, a);
    return "done";
  }

  function connect() {
    if (port) return;
    try { port = chrome.runtime.connect({ name: "field" }); } catch { return; }
    port.onDisconnect.addListener(() => { port = null; cancelRequest(); });
    port.onMessage.addListener(message => {
      if (message.op === "release") { if (request?.id === message.id) cancelRequest(); return; }
      if (message.op !== "prepare" && message.op !== "commit") return;
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
  document.addEventListener("visibilitychange", () => { if (document.hidden) cancelRequest(); });
  connect();
})();
