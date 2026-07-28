(() => {
  const root = document.querySelector("[data-highlight-catalog]");
  if (!root) return;
  const cards = [...root.querySelectorAll(".highlight-card")];
  const boxes = cards.map(card => card.querySelector("input[type=checkbox]"));
  const maximum = Math.min(10, Number(root.dataset.maximum) || 10);
  const grid = root.querySelector(".highlight-grid");
  let category = "All";
  let demo = "All";
  const applyFilters = () => {
    cards.forEach(card => {
      const categoryMatch = category === "All" ||
        (category === "Recommended"
          ? card.dataset.recommended === "true"
          : card.dataset.type === category);
      const demoMatch = demo === "All" || card.dataset.demo === demo;
      card.hidden = !(categoryMatch && demoMatch);
    });
  };
  const update = () => {
    const selected = cards.filter(card => card.querySelector("input").checked);
    root.querySelector("#selected-count").textContent = selected.length;
    const milliseconds = selected.reduce(
      (sum, card) => sum + Number(card.dataset.duration), 0);
    const seconds = Math.round(milliseconds / 1000);
    root.querySelector("#selected-duration").textContent =
      `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
    cards.forEach(card =>
      card.classList.toggle("selected", card.querySelector("input").checked));
  };
  root.querySelectorAll("[data-filter]").forEach(button =>
    button.addEventListener("click", () => {
      category = button.dataset.filter;
      applyFilters();
    }));
  root.querySelectorAll("[data-select]").forEach(button =>
    button.addEventListener("click", () => {
      const mode = button.dataset.select;
      if (mode === "recommended" || mode === "none" || mode?.startsWith("top"))
        boxes.forEach(box => { box.checked = false; });
      if (mode === "recommended")
        cards.filter(card => card.dataset.recommended === "true")
          .forEach(card => { card.querySelector("input").checked = true; });
      if (mode === "visible")
        cards.filter(card => !card.hidden)
          .forEach(card => { card.querySelector("input").checked = true; });
      if (mode === "clear-visible")
        cards.filter(card => !card.hidden)
          .forEach(card => { card.querySelector("input").checked = false; });
      if (mode?.startsWith("top")) {
        const count = Math.min(maximum, Number(mode.slice(3)));
        [...cards].sort((a, b) => Number(b.dataset.score) - Number(a.dataset.score))
          .slice(0, count)
          .forEach(card => { card.querySelector("input").checked = true; });
      }
      update();
    }));
  boxes.forEach(box => box.addEventListener("change", update));
  root.querySelector("#demo-filter")?.addEventListener("change", event => {
    demo = event.target.value;
    applyFilters();
  });
  root.querySelector("#catalog-sort")?.addEventListener("change", event => {
    const mode = event.target.value;
    const sorted = [...cards].sort((left, right) => {
      if (mode === "round")
        return Number(left.dataset.round) - Number(right.dataset.round) ||
          Number(left.dataset.tick) - Number(right.dataset.tick);
      if (mode === "chronological")
        return left.dataset.demo.localeCompare(right.dataset.demo) ||
          Number(left.dataset.tick) - Number(right.dataset.tick);
      return Number(right.dataset.score) - Number(left.dataset.score) ||
        Number(left.dataset.tick) - Number(right.dataset.tick);
    });
    sorted.forEach(card => grid.append(card));
  });
  root.querySelector("[data-select=recommended]")?.click();
  update();
})();
