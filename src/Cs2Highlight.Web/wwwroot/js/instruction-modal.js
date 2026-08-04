const modal = document.querySelector("[data-help-modal]");
if (modal) {
  const dialog = modal.querySelector("[role=dialog]");
  let restoreFocus = null;
  const focusable = () => [...dialog.querySelectorAll("button, a, input, [tabindex]:not([tabindex='-1'])")];
  const close = () => {
    modal.hidden = true;
    document.body.classList.remove("modal-open");
    restoreFocus?.focus();
  };
  const open = event => {
    restoreFocus = event.currentTarget;
    modal.hidden = false;
    document.body.classList.add("modal-open");
    dialog.focus();
  };
  document.querySelectorAll("[data-help-modal-open]").forEach(button => button.addEventListener("click", open));
  modal.querySelectorAll("[data-help-modal-close]").forEach(button => button.addEventListener("click", close));
  document.addEventListener("keydown", event => {
    if (modal.hidden) return;
    if (event.key === "Escape") { event.preventDefault(); close(); return; }
    if (event.key !== "Tab") return;
    const items = focusable();
    if (!items.length) return;
    const first = items[0]; const last = items[items.length - 1];
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });
}
