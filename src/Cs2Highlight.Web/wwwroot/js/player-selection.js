const playerRoot = document.querySelector("[data-player-selection]");
if (playerRoot) {
  const cards = [...playerRoot.querySelectorAll("[data-player-card]")];
  const search = playerRoot.querySelector("[data-player-search]");
  const sort = playerRoot.querySelector("[data-player-sort]");
  const submit = playerRoot.querySelector("[data-player-submit]");
  const grid = playerRoot.querySelector("[data-player-grid]");

  const refresh = () => {
    const query = search.value.trim().toLowerCase();
    cards.forEach(card => {
      card.hidden = !card.dataset.search.includes(query);
    });
  };

  const sortCards = () => {
    const key = sort.value;
    const sorted = [...cards].sort((left, right) => {
      if (key === "kills") return Number(right.dataset.kills) - Number(left.dataset.kills);
      if (key === "matches") return Number(right.dataset.matches) - Number(left.dataset.matches);
      if (key === "name") return left.dataset.search.localeCompare(right.dataset.search, "ru");
      return Number(right.dataset.moments) - Number(left.dataset.moments);
    });
    sorted.forEach(card => grid.append(card));
  };

  search.addEventListener("input", refresh);
  sort.addEventListener("change", sortCards);
  cards.forEach(card => {
    card.querySelector("input").addEventListener("change", () => {
      submit.disabled = !playerRoot.querySelector("input[name=SteamId]:checked");
    });
  });
}
