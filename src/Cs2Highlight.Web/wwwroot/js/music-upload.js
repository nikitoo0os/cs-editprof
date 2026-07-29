import { showToast } from "./toast.js";

const musicRoot = document.querySelector("[data-music-upload]");
if (musicRoot) {
  const input = musicRoot.querySelector("input[type=file]");
  const fileName = musicRoot.querySelector("[data-music-file-name]");
  input?.addEventListener("change", () => {
    const file = input.files?.[0];
    if (!file) return;
    fileName.textContent = file.name;
    showToast("Музыкальный трек добавлен", "success");
  });
}
