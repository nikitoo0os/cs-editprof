import { copyFile, mkdir } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = new URL("../", import.meta.url);
const webRoot = new URL("../src/Cs2Highlight.Web/wwwroot/", import.meta.url);

const files = [
  [
    "node_modules/@microsoft/signalr/dist/browser/signalr.min.js",
    "js/vendor/signalr.min.js"
  ],
  [
    "node_modules/flowbite/dist/flowbite.min.js",
    "js/vendor/flowbite.min.js"
  ]
];

const icons = [
  "alert-triangle",
  "badge-dollar-sign",
  "check",
  "check-circle-2",
  "chevron-down",
  "chevron-right",
  "circle-help",
  "circle-x",
  "clock-3",
  "cloud-upload",
  "credit-card",
  "crosshair",
  "download",
  "file-video-2",
  "files",
  "film",
  "filter",
  "gamepad-2",
  "headphones",
  "info",
  "list-filter",
  "loader-circle",
  "menu",
  "music-2",
  "pause",
  "play",
  "plus",
  "rotate-ccw",
  "search",
  "settings-2",
  "shield-check",
  "sparkles",
  "trash-2",
  "upload-cloud",
  "user-round",
  "users-round",
  "video",
  "volume-2",
  "wand-sparkles",
  "x"
];

for (const [source, target] of files) {
  const destination = new URL(target, webRoot);
  await mkdir(dirname(fileURLToPath(destination)), { recursive: true });
  await copyFile(new URL(source, root), destination);
}

for (const icon of icons) {
  const source = new URL(
    `node_modules/lucide-static/icons/${icon}.svg`,
    root
  );
  const destination = new URL(`assets/icons/${icon}.svg`, webRoot);
  await mkdir(dirname(fileURLToPath(destination)), { recursive: true });
  await copyFile(source, destination);
}

console.log(`Synced ${files.length} vendor files and ${icons.length} Lucide icons.`);
