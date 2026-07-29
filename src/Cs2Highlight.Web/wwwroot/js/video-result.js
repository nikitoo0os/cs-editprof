import { showToast } from "./toast.js";

document.querySelector("[data-video-result] video")?.addEventListener("play", () => {
  showToast("Воспроизведение началось");
});
