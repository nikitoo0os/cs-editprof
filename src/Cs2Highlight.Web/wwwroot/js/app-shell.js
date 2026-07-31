import { showToast } from "./toast.js";

const themeStorageKey = "cshighlighter-theme-v1";
const themeColors = {
  redline: "#090a0c",
  printstream: "#f4f6f8"
};

function applyTheme(theme, persist = false) {
  const nextTheme = theme === "printstream" ? "printstream" : "redline";
  document.documentElement.dataset.theme = nextTheme;
  document.querySelector("[data-theme-color]")?.setAttribute("content", themeColors[nextTheme]);
  document.querySelectorAll("[data-theme-choice]").forEach(button => {
    const selected = button.dataset.themeChoice === nextTheme;
    button.setAttribute("aria-pressed", String(selected));
  });

  if (persist) {
    try {
      localStorage.setItem(themeStorageKey, nextTheme);
    } catch { }
  }
}

applyTheme(document.documentElement.dataset.theme);

document.querySelectorAll("[data-theme-choice]").forEach(button => {
  button.addEventListener("click", () => applyTheme(button.dataset.themeChoice, true));
});

document.querySelectorAll("form[data-loading-form]").forEach(form => {
  form.addEventListener("submit", () => {
    const button = form.querySelector("button[type=submit]");
    if (!button || button.disabled) return;
    button.disabled = true;
    button.setAttribute("aria-busy", "true");
    const label = button.dataset.loadingLabel || "Продолжаем…";
    button.replaceChildren();
    const spinner = document.createElement("img");
    spinner.className = "ui-icon";
    spinner.src = "/assets/icons/loader-circle.svg";
    spinner.alt = "";
    button.append(spinner, document.createTextNode(label));
  });
});

document.querySelectorAll('a[href*="download=true"]').forEach(link => {
  link.addEventListener("click", () => {
    showToast("Скачивание MP4 началось", "success");
  });
});

document.querySelectorAll("[data-toast-on-load]").forEach(element => {
  showToast(element.dataset.toastOnLoad, element.dataset.toastTone || "info");
});
