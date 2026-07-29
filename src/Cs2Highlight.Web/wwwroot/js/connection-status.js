import { showToast } from "./toast.js";

const banner = document.querySelector("#connection-banner");
const message = banner?.querySelector("[data-connection-message]");

export function showConnectionIssue() {
  if (!banner || !message) return;
  message.textContent = "Соединение потеряно. Пытаемся восстановить…";
  banner.hidden = false;
}

export function showConnectionRestored() {
  if (!banner || !message) return;
  banner.hidden = true;
  showToast("Соединение восстановлено", "success");
}
