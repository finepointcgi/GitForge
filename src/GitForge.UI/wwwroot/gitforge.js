// GitForge host-agnostic browser helpers: keyboard routing, resizable panes, scrolling, clipboard.
(function () {
  "use strict";

  // Key chords in the canonical form Keymap.cs uses: "ctrl+alt+shift+key", lower case. Shift counts only for letters
  // and named keys ("?" already means Shift+/). The .NET side sends the chords to capture, so only bound keys are taken.
  const namedKeys = new Set(["enter", "escape", "tab", "backspace", "delete", "insert", "home", "end", "pageup", "pagedown",
    "arrowup", "arrowdown", "arrowleft", "arrowright", "contextmenu"]);
  let bindings = new Map(); // chord -> fires inside text fields
  let keyHandler = null;

  function chordOf(e) {
    const key = e.key;
    if (!key) return null;
    let name;
    if (key.length === 1) {
      if (key.charCodeAt(0) < 32) return null;
      name = key === " " ? "space" : key.toLowerCase();
    } else {
      name = key.toLowerCase();
      if (!namedKeys.has(name) && !/^f([1-9]|1[0-9]|2[0-4])$/.test(name)) return null; // Shift, Control, Dead, ...
    }
    const letter = key.length === 1 && key.toLowerCase() !== key.toUpperCase();
    const shift = e.shiftKey && (letter || key.length > 1);
    return (e.ctrlKey || e.metaKey ? "ctrl+" : "") + (e.altKey ? "alt+" : "") + (shift ? "shift+" : "") + name;
  }

  function setKeyBindings(list) {
    bindings = new Map((list || []).map(function (b) { return [b.chord, !!b.inText]; }));
  }

  function isTextTarget(el) {
    if (!el) return false;
    const tag = el.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || el.isContentEditable;
  }

  function dialogOpen() {
    return document.querySelector(".mud-dialog-container") !== null;
  }

  function registerShortcuts(dotnetRef, list) {
    unregisterShortcuts();
    setKeyBindings(list);
    keyHandler = function (e) {
      const chord = chordOf(e);
      if (!chord) return;
      if (chord === "f5") e.preventDefault(); // never reload the app, bound or not
      if (!bindings.has(chord) || dialogOpen()) return;
      const inInput = isTextTarget(e.target);
      if (inInput && !bindings.get(chord)) return; // typing wins (and Ctrl+Z stays native text undo)
      // Let focused buttons and links keep native Space/Enter activation.
      const tag = e.target && e.target.tagName;
      if ((chord === "space" || chord === "enter") && (tag === "BUTTON" || tag === "A")) return;
      if (chord !== "escape") e.preventDefault();
      dotnetRef.invokeMethodAsync("OnKey", {
        key: e.key, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey, meta: e.metaKey, inInput: inInput
      }).catch(function () { /* circuit gone */ });
    };
    document.addEventListener("keydown", keyHandler, true);
    initSplitters();
  }

  function unregisterShortcuts() {
    if (keyHandler) document.removeEventListener("keydown", keyHandler, true);
    keyHandler = null;
  }

  // Splitters: <div class="gf-splitter" data-var="--x" data-target="elementId" data-min data-max data-max-ratio data-invert>
  // data-max-ratio caps the size at that fraction of the target (gitforge.css applies the same cap when the window shrinks).
  let splittersReady = false;
  function initSplitters() {
    if (splittersReady) return;
    splittersReady = true;
    document.addEventListener("pointerdown", function (e) {
      const s = e.target.closest && e.target.closest(".gf-splitter");
      if (!s) return;
      const target = document.getElementById(s.dataset.target);
      if (!target) return;
      e.preventDefault();
      const horizontal = s.classList.contains("gf-splitter-x");
      const invert = s.dataset.invert === "1";
      const min = parseInt(s.dataset.min || "100", 10);
      const max = parseInt(s.dataset.max || "2000", 10);
      const maxRatio = parseFloat(s.dataset.maxRatio || "0");
      try { s.setPointerCapture(e.pointerId); } catch (_) { }
      s.classList.add("dragging");

      function move(ev) {
        const r = target.getBoundingClientRect();
        let size = horizontal
          ? (invert ? r.right - ev.clientX : ev.clientX - r.left)
          : (invert ? r.bottom - ev.clientY : ev.clientY - r.top);
        const extent = horizontal ? r.width : r.height;
        const cap = maxRatio > 0 ? Math.min(max, extent * maxRatio) : max;
        size = Math.round(Math.max(min, Math.min(cap, size)));
        target.style.setProperty(s.dataset.var, size + "px");
      }
      function up() {
        try { s.releasePointerCapture(e.pointerId); } catch (_) { }
        s.classList.remove("dragging");
        document.removeEventListener("pointermove", move);
        document.removeEventListener("pointerup", up);
        document.removeEventListener("pointercancel", up);
        try { localStorage.setItem("gf:" + s.dataset.target + s.dataset.var, target.style.getPropertyValue(s.dataset.var)); } catch (_) { }
      }
      document.addEventListener("pointermove", move);
      document.addEventListener("pointerup", up);
      document.addEventListener("pointercancel", up);
    });

    // Column resizers: <span class="gf-col-resize" data-var data-target data-dir="right|left" data-min data-max>.
    // Width changes by the drag distance from the column's starting width ("left" handles grow when dragged left).
    document.addEventListener("pointerdown", function (e) {
      const h = e.target.closest && e.target.closest(".gf-col-resize");
      if (!h || e.button !== 0) return;
      const target = document.getElementById(h.dataset.target);
      const cell = h.parentElement;
      if (!target || !cell) return;
      e.preventDefault();
      e.stopPropagation();
      const startX = e.clientX;
      const startW = cell.getBoundingClientRect().width;
      const dir = h.dataset.dir === "left" ? -1 : 1;
      const min = parseInt(h.dataset.min || "40", 10);
      const max = parseInt(h.dataset.max || "1200", 10);
      try { h.setPointerCapture(e.pointerId); } catch (_) { } // capture is a nicety; document listeners still track the drag
      h.classList.add("dragging");
      document.body.classList.add("gf-col-resizing");

      function move(ev) {
        const w = Math.max(min, Math.min(max, startW + dir * (ev.clientX - startX)));
        target.style.setProperty(h.dataset.var, Math.round(w) + "px");
      }
      function up() {
        try { h.releasePointerCapture(e.pointerId); } catch (_) { }
        h.classList.remove("dragging");
        document.body.classList.remove("gf-col-resizing");
        document.removeEventListener("pointermove", move);
        document.removeEventListener("pointerup", up);
        document.removeEventListener("pointercancel", up);
        try { localStorage.setItem("gf:" + h.dataset.target + h.dataset.var, target.style.getPropertyValue(h.dataset.var)); } catch (_) { }
      }
      document.addEventListener("pointermove", move);
      document.addEventListener("pointerup", up);
      document.addEventListener("pointercancel", up);
    }, true);

    // Double-click a column border to go back to its default width.
    document.addEventListener("dblclick", function (e) {
      const h = e.target.closest && e.target.closest(".gf-col-resize");
      if (!h) return;
      const target = document.getElementById(h.dataset.target);
      if (target) target.style.removeProperty(h.dataset.var);
      try { localStorage.removeItem("gf:" + h.dataset.target + h.dataset.var); } catch (_) { }
    });

    // Restore saved sizes whenever panes (re)appear.
    const restore = function () {
      document.querySelectorAll(".gf-splitter, .gf-col-resize").forEach(function (s) {
        const target = document.getElementById(s.dataset.target);
        if (!target || target.style.getPropertyValue(s.dataset.var)) return;
        let saved = null;
        try { saved = localStorage.getItem("gf:" + s.dataset.target + s.dataset.var); } catch (_) { }
        if (saved) target.style.setProperty(s.dataset.var, saved);
      });
    };
    new MutationObserver(restore).observe(document.body, { childList: true, subtree: true });
    restore();
  }

  function scrollRowIntoView(container, index, rowHeight) {
    if (!container || index < 0) return;
    const top = index * rowHeight;
    const bottom = top + rowHeight;
    if (top < container.scrollTop) container.scrollTop = top;
    else if (bottom > container.scrollTop + container.clientHeight) container.scrollTop = bottom - container.clientHeight;
  }

  /** Clears every saved column width for a resizable grid (header right-click → Reset column widths). */
  function resetColumns(targetId) {
    const target = document.getElementById(targetId);
    document.querySelectorAll('.gf-col-resize[data-target="' + targetId + '"]').forEach(function (h) {
      if (target) target.style.removeProperty(h.dataset.var);
    });
    try {
      Object.keys(localStorage).filter(function (k) { return k.indexOf("gf:" + targetId + "--gf-col-") === 0; })
        .forEach(function (k) { localStorage.removeItem(k); });
    } catch (_) { }
  }

  function scrollIntoViewById(id) {
    const el = document.getElementById(id);
    if (el) el.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }

  function scrollToBottom(el) {
    if (el) el.scrollTop = el.scrollHeight;
  }

  function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) return navigator.clipboard.writeText(text);
    const ta = document.createElement("textarea");
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand("copy");
    ta.remove();
  }

  window.gitforge = { registerShortcuts, unregisterShortcuts, setKeyBindings, scrollRowIntoView, scrollIntoViewById, scrollToBottom, copyText, resetColumns };
})();
