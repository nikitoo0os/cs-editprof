import { showToast } from "./toast.js";

const root = document.querySelector("[data-upload-dropzone]");
if (root) {
  const input = root.querySelector("input[type=file]");
  const zone = root.querySelector(".upload-zone");
  const list = root.querySelector("[data-file-list]");
  const summary = root.querySelector("[data-file-summary]");
  const form = root.closest("form");
  const maximumFiles = Number(root.dataset.maxFiles || 10);
  const maximumBytes = Number(root.dataset.maxFileBytes || 0);
  let selected = [];

  const formatBytes = value => {
    if (value < 1024 * 1024) return `${Math.max(1, Math.round(value / 1024))} КБ`;
    if (value < 1024 * 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} МБ`;
    return `${(value / 1024 / 1024 / 1024).toFixed(2)} ГБ`;
  };

  const syncInput = () => {
    const transfer = new DataTransfer();
    selected.forEach(file => transfer.items.add(file));
    input.files = transfer.files;
  };

  const render = () => {
    list.replaceChildren();
    const total = selected.reduce((sum, file) => sum + file.size, 0);
    summary.textContent = selected.length
      ? `${selected.length} ${selected.length === 1 ? "демка" : "демки"} · ${formatBytes(total)}`
      : "Файлы ещё не выбраны";

    selected.forEach((file, index) => {
      const row = document.createElement("div");
      row.className = "file-row";

      const icon = document.createElement("img");
      icon.className = "ui-icon ui-icon--brand";
      icon.src = "/assets/icons/file-video-2.svg";
      icon.alt = "";

      const name = document.createElement("span");
      name.className = "file-row__name";
      name.textContent = file.name;

      const size = document.createElement("span");
      size.className = "file-row__size";
      size.textContent = formatBytes(file.size);

      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "icon-button";
      remove.setAttribute("aria-label", `Удалить ${file.name}`);
      const removeIcon = document.createElement("img");
      removeIcon.className = "ui-icon";
      removeIcon.src = "/assets/icons/trash-2.svg";
      removeIcon.alt = "";
      remove.append(removeIcon);
      remove.addEventListener("click", () => {
        selected.splice(index, 1);
        syncInput();
        render();
        showToast("Файл удалён из списка");
      });

      row.append(icon, name, size, remove);
      list.append(row);
    });
  };

  const addFiles = files => {
    let duplicateFound = false;
    let invalidFound = false;
    for (const file of files) {
      const isDemo = file.name.toLowerCase().endsWith(".dem");
      const isTooLarge = maximumBytes > 0 && file.size > maximumBytes;
      if (!isDemo || isTooLarge) {
        invalidFound = true;
        continue;
      }
      if (selected.some(item => item.name === file.name && item.size === file.size)) {
        duplicateFound = true;
        continue;
      }
      if (selected.length < maximumFiles) selected.push(file);
    }
    syncInput();
    render();
    if (duplicateFound) showToast("Дубликат пропущен", "info");
    if (invalidFound) showToast("Поддерживаются только подходящие файлы .dem", "error");
    if (selected.length) showToast("Демки добавлены", "success");
  };

  input.addEventListener("change", () => {
    selected = [];
    addFiles([...input.files]);
  });
  for (const eventName of ["dragenter", "dragover"]) {
    zone.addEventListener(eventName, event => {
      event.preventDefault();
      zone.classList.add("is-dragging");
    });
  }
  for (const eventName of ["dragleave", "drop"]) {
    zone.addEventListener(eventName, event => {
      event.preventDefault();
      zone.classList.remove("is-dragging");
    });
  }
  zone.addEventListener("drop", event => addFiles([...event.dataTransfer.files]));

  form?.addEventListener("submit", event => {
    if (!selected.length) {
      event.preventDefault();
      showToast("Добавьте хотя бы одну демку", "error");
      input.focus();
      return;
    }
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
      showToast("Не удалось загрузить демку. Попробуйте ещё раз.", "error");
      window.location.reload();
    });
    request.addEventListener("error", () => {
      showToast("Соединение прервалось во время загрузки демки.", "error");
      window.location.reload();
    });
    request.send(new FormData(form));
  });
}
