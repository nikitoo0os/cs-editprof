(() => {
  const storageKey = "cshighlighter-theme-v1";
  let theme = document.documentElement.dataset.theme === "printstream" ? "printstream" : "redline";

  try {
    const savedTheme = localStorage.getItem(storageKey);
    if (savedTheme === "redline" || savedTheme === "printstream") theme = savedTheme;
  } catch { }

  document.documentElement.dataset.theme = theme;
})();
