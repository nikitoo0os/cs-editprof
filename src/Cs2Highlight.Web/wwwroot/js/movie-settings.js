const settings = document.querySelector("[data-movie-settings]");
if (settings) {
  settings.querySelectorAll("input[type=range]").forEach(input => {
    const output = settings.querySelector(`[data-range-value="${input.name}"]`);
    const update = () => { if (output) output.textContent = input.value; };
    input.addEventListener("input", update);
    update();
  });

  const directorPanel = settings.querySelector("[data-cinematic-director-settings]");
  const styleInputs = settings.querySelectorAll("input[name=MovieStyle]");
  const updateDirectorPanel = () => {
    const selected = settings.querySelector("input[name=MovieStyle]:checked");
    if (directorPanel) {
      directorPanel.hidden = selected?.value !== "CinematicDirector";
    }
  };
  styleInputs.forEach(input => input.addEventListener("change", updateDirectorPanel));
  updateDirectorPanel();
}
