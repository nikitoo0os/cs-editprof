export function showToast(message, tone = "info", timeout = 4200) {
  const host = document.querySelector("#toast-host");
  if (!host || !message) return;

  const icon = tone === "success"
    ? "check-circle-2"
    : tone === "error"
      ? "alert-triangle"
      : "info";
  const toast = document.createElement("div");
  toast.className = "toast";
  toast.setAttribute("role", tone === "error" ? "alert" : "status");

  const image = document.createElement("img");
  image.className = tone === "success"
    ? "ui-icon ui-icon--brand"
    : "ui-icon";
  image.src = `/assets/icons/${icon}.svg`;
  image.alt = "";

  const text = document.createElement("span");
  text.textContent = message;

  const close = document.createElement("button");
  close.className = "icon-button";
  close.type = "button";
  close.setAttribute("aria-label", "Закрыть уведомление");
  const closeIcon = document.createElement("img");
  closeIcon.className = "ui-icon";
  closeIcon.src = "/assets/icons/x.svg";
  closeIcon.alt = "";
  close.append(closeIcon);

  const dismiss = () => toast.remove();
  close.addEventListener("click", dismiss);
  toast.append(image, text, close);
  host.append(toast);
  window.setTimeout(dismiss, timeout);
}

window.cshighlighterToast = showToast;
