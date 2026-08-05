import { showToast } from "./toast.js";

const musicRoot = document.querySelector("[data-music-upload]");
if (musicRoot) {
  const input = musicRoot.querySelector("input[type=file]");
  const fileName = musicRoot.querySelector("[data-music-file-name]");
  const form = musicRoot.querySelector("form[enctype='multipart/form-data']");
  input?.addEventListener("change", () => {
    const file = input.files?.[0];
    if (!file) return;
    fileName.textContent = file.name;
    showToast("Музыкальный трек добавлен", "success");
  });

  form?.addEventListener("submit", event => {
    const file = input?.files?.[0];
    if (!file) return;
    event.preventDefault();
    const progress = form.querySelector("[data-upload-progress]");
    const progressBar = form.querySelector("[data-upload-progress-bar]");
    const progressLabel = form.querySelector("[data-upload-progress-label]");
    const progressTrack = progressBar?.parentElement;
    if (progress) progress.hidden = false;
    const request = new XMLHttpRequest();
    request.open(form.method || "POST", form.action || window.location.href);
    request.upload.addEventListener("progress", uploadEvent => {
      if (!uploadEvent.lengthComputable) return;
      const percent = Math.min(100, Math.round(
        uploadEvent.loaded / uploadEvent.total * 100));
      if (progressBar) progressBar.style.width = `${percent}%`;
      if (progressLabel) progressLabel.textContent = `${percent}%`;
      progressTrack?.setAttribute("aria-valuenow", String(percent));
    });
    request.addEventListener("load", () => {
      if (request.status >= 200 && request.status < 400) {
        window.location.assign(request.responseURL || window.location.href);
        return;
      }
      showToast("Не удалось загрузить музыку. Попробуйте ещё раз.", "error");
      window.location.reload();
    });
    request.addEventListener("error", () => {
      showToast("Соединение прервалось во время загрузки музыки.", "error");
      window.location.reload();
    });
    request.send(new FormData(form));
  });
}
