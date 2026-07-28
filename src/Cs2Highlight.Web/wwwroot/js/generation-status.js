(() => {
  const root = document.querySelector("[data-generation-status]");
  if (!root) return;

  const terminal = new Set([
    "Completed",
    "CompletedWithWarnings",
    "Cancelled",
    "Failed",
    "Expired"
  ]);
  const id = root.dataset.publicId;
  const elements = {
    status: document.querySelector("#status"),
    stage: document.querySelector("#generation-stage"),
    bar: document.querySelector("#bar"),
    progress: document.querySelector("#progress-value"),
    demos: document.querySelector("#demo-count"),
    players: document.querySelector("#player-count"),
    highlights: document.querySelector("#highlight-count"),
    action: document.querySelector("#next-action"),
    video: document.querySelector("#video-result"),
    error: document.querySelector("#generation-error"),
    events: document.querySelector("#event-feed")
  };

  const actionLabel = status =>
    status === "AwaitingPlayerSelection"
      ? "Выбрать игрока"
      : status === "AwaitingHighlightSelection"
        ? "Выбрать моменты"
        : "Музыка и стиль";

  function render(state) {
    elements.status.textContent = state.status;
    elements.stage.textContent = state.stage;
    elements.bar.style.width = `${state.progressPercent}%`;
    elements.progress.textContent = state.progressPercent;
    elements.demos.textContent = state.demoCount;
    elements.players.textContent = state.playerCount;
    elements.highlights.textContent = state.highlightCount;

    elements.action.replaceChildren();
    if (state.actionUrl) {
      const link = document.createElement("a");
      link.className = "button";
      link.href = state.actionUrl;
      link.textContent = actionLabel(state.status);
      elements.action.append(link);
    }

    if (state.completed) elements.video.hidden = false;
    if (state.errorCode) {
      elements.error.hidden = false;
      elements.error.textContent =
        `${state.errorCode}: ${state.errorMessage || ""}`;
    } else {
      elements.error.hidden = true;
      elements.error.textContent = "";
    }

    elements.events.replaceChildren();
    for (const item of state.events || []) {
      const line = document.createElement("p");
      line.className = "status-event";
      line.textContent = `${item.progressPercent}% · ${item.message}`;
      elements.events.append(line);
    }
  }

  async function refresh() {
    if (document.visibilityState !== "visible") return false;
    try {
      const response = await fetch(
        `/api/generations/${encodeURIComponent(id)}`,
        { headers: { Accept: "application/json" }, cache: "no-store" }
      );
      if (!response.ok) return false;
      const state = await response.json();
      render(state);
      return terminal.has(state.status);
    } catch {
      return false;
    }
  }

  let timer;
  async function tick() {
    if (await refresh()) {
      window.clearInterval(timer);
    }
  }
  timer = window.setInterval(tick, 3000);
  void tick();

  if (window.signalR) {
    const connection = new window.signalR.HubConnectionBuilder()
      .withUrl("/hubs/generations")
      .withAutomaticReconnect()
      .build();
    connection.on("progress", progress => {
      const current = Number(elements.progress.textContent || "0");
      if (progress.progressPercent < current) return;
      elements.status.textContent = progress.status;
      elements.stage.textContent = progress.stage;
      elements.bar.style.width = `${progress.progressPercent}%`;
      elements.progress.textContent = progress.progressPercent;
      void refresh();
    });
    connection.onreconnected(() => {
      void connection.invoke("Subscribe", id);
      void refresh();
    });
    connection.start()
      .then(() => connection.invoke("Subscribe", id))
      .catch(() => {
        // Polling above remains active when SignalR or the CDN is unavailable.
      });
  }
})();
