const settings = document.querySelector("[data-movie-settings]");
if (settings) {
  settings.querySelectorAll("input[type=range]").forEach(input => {
    const output = settings.querySelector(`[data-range-value="${input.name}"]`);
    const update = () => { if (output) output.textContent = input.value; };
    input.addEventListener("input", update);
    update();
  });
}
