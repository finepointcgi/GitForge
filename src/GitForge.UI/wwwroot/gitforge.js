// GitForge host-agnostic browser helpers: keyboard routing, resizable panes, scrolling, clipboard.
(function () {
  "use strict";

  const ctrlKeys = new Set(["1", "2", "3", "o", "f", "l", "p", "b", "s", "`", "enter"]);
  const plainKeys = new Set(["ArrowDown", "ArrowUp", "Enter", "j", "k", "g", "G", "h", "l", "s", "u", "d", " ", "/", "?"]);
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
      s.setPointerCapture(e.pointerId);
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
        s.releasePointerCapture(e.pointerId);
        s.classList.remove("dragging");
        s.removeEventListener("pointermove", move);
        s.removeEventListener("pointerup", up);
        try { localStorage.setItem("gf:" + s.dataset.target + s.dataset.var, target.style.getPropertyValue(s.dataset.var)); } catch (_) { }
      }
      s.addEventListener("pointermove", move);
      s.addEventListener("pointerup", up);
    });

    // Restore saved sizes whenever panes (re)appear.
    const restore = function () {
      document.querySelectorAll(".gf-splitter").forEach(function (s) {
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

  window.gitforge = { registerShortcuts, unregisterShortcuts, scrollRowIntoView, scrollToBottom, copyText };
})();
