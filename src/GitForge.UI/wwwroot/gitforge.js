// GitForge host-agnostic browser helpers: keyboard routing, resizable panes, scrolling, clipboard.
(function () {
  "use strict";

  const ctrlKeys = new Set(["1", "2", "3", "4", "h", "o", "f", "l", "p", "b", "s", "`", "enter", "tab", "pagedown", "pageup", "t", "w"]);
  const plainKeys = new Set(["ArrowDown", "ArrowUp", "Enter", "j", "k", "g", "G", "h", "l", "s", "u", "d", " ", "/", "?", "m", "t", "b", "n", "p"]);
  let keyHandler = null;

  function isTextTarget(el) {
    if (!el) return false;
    const tag = el.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || el.isContentEditable;
  }

  function dialogOpen() {
    return document.querySelector(".mud-dialog-container") !== null;
  }

  function registerShortcuts(dotnetRef) {
    unregisterShortcuts();
    keyHandler = function (e) {
      const ctrl = e.ctrlKey || e.metaKey;
      const inInput = isTextTarget(e.target);
      const key = e.key;
      let handled = false;

      if (ctrl && (key.toLowerCase() === "z" || key.toLowerCase() === "y")) handled = !inInput && !dialogOpen(); // keep native text undo
      else if (ctrl && ctrlKeys.has(key.toLowerCase())) handled = !dialogOpen();
      else if (key === "F5") handled = true;
      else if (key === "Escape") handled = !dialogOpen();
      else if (!ctrl && !e.altKey && !inInput && !dialogOpen() && plainKeys.has(key)) {
        // Let focused buttons and links keep native Space/Enter activation.
        const tag = e.target && e.target.tagName;
        handled = !((key === " " || key === "Enter") && (tag === "BUTTON" || tag === "A"));
      }

      if (!handled) return;
      if (key !== "Escape") e.preventDefault();
      dotnetRef.invokeMethodAsync("OnKey", {
        key: key, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey, meta: e.metaKey, inInput: inInput
      }).catch(function () { /* circuit gone */ });
    };
    document.addEventListener("keydown", keyHandler, true);
    initSplitters();
  }

  function unregisterShortcuts() {
    if (keyHandler) document.removeEventListener("keydown", keyHandler, true);
    keyHandler = null;
  }

  // Splitters: <div class="gf-splitter" data-var="--x" data-target="elementId" data-min data-max data-invert>
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
      try { s.setPointerCapture(e.pointerId); } catch (_) { }
      s.classList.add("dragging");

      function move(ev) {
        const r = target.getBoundingClientRect();
        let size = horizontal
          ? (invert ? r.right - ev.clientX : ev.clientX - r.left)
          : (invert ? r.bottom - ev.clientY : ev.clientY - r.top);
        size = Math.max(min, Math.min(max, size));
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

  window.gitforge = { registerShortcuts, unregisterShortcuts, scrollRowIntoView, scrollIntoViewById, scrollToBottom, copyText, resetColumns };
})();
