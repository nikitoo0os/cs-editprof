import { showToast } from "./toast.js";

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
