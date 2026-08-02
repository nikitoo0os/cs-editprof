import { showToast } from "./toast.js";

const root = document.querySelector("[data-highlight-catalog]");
if (root) {
  const cards = [...root.querySelectorAll(".highlight-card")];
  const boxes = cards.map(card => card.querySelector("input[type=checkbox]"));
  const grid = root.querySelector(".highlight-grid");
  const submit = root.querySelector("[data-selection-submit]");
  const maximumCount = Number(root.dataset.maxSelectionCount || 0);
  const maximumDuration = Number(root.dataset.maxSelectionDuration || 0);
  const transitionDuration = Number(root.dataset.transitionDuration || 0);
  let category = "All";
  let demo = "All";
  let query = "";

  const applyFilters = () => {
    cards.forEach(card => {
      const categoryMatch = category === "All" ||
        (category === "Recommended"
          ? card.dataset.recommended === "true"
          : card.dataset.type === category);
      const demoMatch = demo === "All" || card.dataset.demo === demo;
      const queryMatch = !query ||
        `${card.dataset.demo} ${card.textContent}`.toLowerCase().includes(query);
      card.hidden = !(categoryMatch && demoMatch && queryMatch);
    });
  };

  const timelineDuration = selectedCards => Math.max(
    0,
    selectedCards.reduce(
      (sum, card) => sum + Number(card.dataset.duration), 0) -
      Math.max(0, selectedCards.length - 1) * transitionDuration
  );

  const update = () => {
    const selected = cards.filter(card => card.querySelector("input").checked);
    root.querySelectorAll("[data-selected-count]").forEach(
      element => { element.textContent = selected.length; }
    );
    const milliseconds = timelineDuration(selected);
    const seconds = Math.round(milliseconds / 1000);
    const duration = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
    root.querySelectorAll("[data-selected-duration]").forEach(
      element => { element.textContent = duration; }
    );
    cards.forEach(card =>
      card.classList.toggle("selected", card.querySelector("input").checked));
    boxes.forEach(box => {
      if (box.checked) {
        box.disabled = false;
        return;
      }
      const card = box.closest(".highlight-card");
      const candidateDuration = card
        ? timelineDuration([...selected, card])
        : milliseconds;
      box.disabled = (maximumCount > 0 && selected.length >= maximumCount) ||
        (maximumDuration > 0 && candidateDuration > maximumDuration);
    });
    submit.disabled = selected.length === 0;
  };

  const canSelect = (card, selectedCards) => {
    if (maximumCount > 0 && selectedCards.length >= maximumCount) return false;
    const duration = timelineDuration([...selectedCards, card]);
    return maximumDuration <= 0 ||
      duration <= maximumDuration;
  };

  const selectWithinMusicLimit = candidates => {
    const selected = [];
    candidates.forEach(card => {
      if (!canSelect(card, selected)) return;
      card.querySelector("input").checked = true;
      selected.push(card);
    });
    return selected.length;
  };

  root.querySelectorAll("[data-filter]").forEach(button =>
    button.addEventListener("click", () => {
      category = button.dataset.filter;
      root.querySelectorAll("[data-filter]").forEach(item =>
        item.setAttribute("aria-selected", String(item === button)));
      applyFilters();
    }));

  root.querySelectorAll("[data-select]").forEach(button =>
    button.addEventListener("click", () => {
      const mode = button.dataset.select;
      if (mode === "recommended" || mode === "none") {
        boxes.forEach(box => { box.checked = false; });
      }
      if (mode === "recommended") {
        selectWithinMusicLimit(
          cards.filter(card => card.dataset.recommended === "true"));
        showToast("Рекомендованные моменты выбраны", "success");
      }
      if (mode === "visible") {
        boxes.forEach(box => { box.checked = false; });
        selectWithinMusicLimit(cards.filter(card => !card.hidden));
      }
      if (mode === "clear-visible") {
        cards.filter(card => !card.hidden)
          .forEach(card => { card.querySelector("input").checked = false; });
      }
      if (mode === "none") boxes.forEach(box => { box.checked = false; });
      update();
    }));

  boxes.forEach(box => box.addEventListener("change", () => {
    if (box.checked) {
      const selected = cards.filter(card =>
        card.querySelector("input").checked &&
        card.querySelector("input") !== box);
      const card = box.closest(".highlight-card");
      if (!card || !canSelect(card, selected)) {
        box.checked = false;
        const suffix = maximumCount > 0
          ? `: не больше ${maximumCount} моментов`
          : "";
        showToast(`Лимит текущего трека${suffix}`, "warning");
      }
    }
    update();
  }));

  root.querySelector("#demo-filter")?.addEventListener("change", event => {
    demo = event.target.value;
    applyFilters();
  });
  root.querySelector("[data-highlight-search]")?.addEventListener("input", event => {
    query = event.target.value.trim().toLowerCase();
    applyFilters();
  });
  root.querySelector("#catalog-sort")?.addEventListener("change", event => {
    const mode = event.target.value;
    const sorted = [...cards].sort((left, right) => {
      if (mode === "round")
        return Number(left.dataset.round) - Number(right.dataset.round) ||
          Number(left.dataset.tick) - Number(right.dataset.tick);
      if (mode === "chronological")
        return left.dataset.demo.localeCompare(right.dataset.demo, "ru") ||
          Number(left.dataset.tick) - Number(right.dataset.tick);
      return Number(right.dataset.score) - Number(left.dataset.score) ||
        Number(left.dataset.tick) - Number(right.dataset.tick);
    });
    sorted.forEach(card => grid.append(card));
  });

  update();
}
