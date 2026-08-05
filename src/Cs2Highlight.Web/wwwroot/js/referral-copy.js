import { showToast } from "./toast.js";

const button = document.querySelector("[data-copy-referral]");

if (button) {
  button.addEventListener("click", async () => {
    const url = button.dataset.referralUrl;
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
    } catch {
      const input = document.createElement("textarea");
      input.value = url;
      input.setAttribute("readonly", "");
      input.style.position = "fixed";
      input.style.opacity = "0";
      document.body.append(input);
      input.select();
      document.execCommand("copy");
      input.remove();
    }
    button.textContent = "Ссылка скопирована";
    showToast("Ссылка для тиммейта скопирована", "success");
    window.setTimeout(() => {
      button.textContent = "Скопировать ссылку";
    }, 2200);
  });
}
