import { showConnectionIssue, showConnectionRestored } from "./connection-status.js";
import { showToast } from "./toast.js";

const root = document.querySelector("[data-generation-status]");
if (root) {
  const terminal = new Set([
    "Completed",
    "CompletedWithWarnings",
    "Cancelled",
    "Failed",
    "Expired"
  ]);
  const labels = {
    Draft: "Подготовка",
    Uploading: "Загружаем демки",
    Uploaded: "Демки загружены",
    QueuedForAnalysis: "Ожидаем анализ",
    Analyzing: "Анализируем демки",
    BuildingHighlightCatalog: "Ищем лучшие убийства",
    AwaitingPlayerSelection: "Выберите игрока",
    AwaitingHighlightSelection: "Выберите моменты",
    AwaitingMusicUpload: "Добавьте музыку",
    AnalyzingMusic: "Анализируем музыку",
    AnalyzingMusicStructure: "Разбираем структуру трека",
    AwaitingMovieConfiguration: "Настройте мувик",
    ValidatingMoviePlan: "Проверяем настройки",
    SelectingMusicExcerpt: "Выбираем лучший фрагмент трека",
    AnalyzingGameplayTimeline: "Анализируем движение в демке",
    DetectingBroll: "Ищем игровые перебивки",
    PlanningNarrative: "Строим драматургию",
    PlanningCameraShots: "Планируем камеры",
    AwaitingPayment: "Ожидаем оплату",
    QueuedForGeneration: "Мувик в очереди",
    PreparingRenderPlan: "Готовим монтаж",
    SelectingHighlights: "Собираем моменты",
    RenderingClips: "Рендерим моменты",
    RenderingHighlights: "Рендерим основные highlights",
    VerifyingClips: "Проверяем клипы",
    PlanningMusicEdit: "Подстраиваем монтаж под трек",
    ApplyingTimeWarp: "Подстраиваем моменты под ритм",
    ApplyingEffects: "Добавляем эффекты",
    SynchronizingPeaks: "Синхронизируем убийства с пиками",
    RenderingCameraPreviews: "Рендерим превью камер",
    ValidatingCameraShots: "Проверяем траектории камер",
    RenderingCinematicShots: "Рендерим cinematic shots",
    ComposingVideo: "Собираем финальный монтаж",
    ComposingCinematicTimeline: "Собираем cinematic timeline",
    MixingAudio: "Смешиваем звук",
    MixingNarrativeAudio: "Смешиваем звук по структуре трека",
    ApplyingColorGrade: "Применяем цвет",
    ApplyingNarrativeColor: "Применяем цветовую драматургию",
    VerifyingCinematicMovie: "Проверяем cinematic movie",
    VerifyingOutput: "Проверяем готовое видео",
    Completed: "Готово",
    CompletedWithWarnings: "Готово с замечаниями",
    Cancelling: "Отменяем",
    Cancelled: "Отменено",
    Failed: "Нужна помощь",
    Expired: "Результат удалён"
  };
  const eventLabel = message => {
    const value = String(message || "").toLowerCase();
    if (value.includes("render")) return "Рендерим выбранные моменты";
    if (value.includes("music") || value.includes("audio")) return "Подстраиваем монтаж и звук под музыку";
    if (value.includes("effect")) return "Добавляем видеоэффекты";
    if (value.includes("color")) return "Настраиваем цвет";
    if (value.includes("verif")) return "Проверяем результат";
    if (value.includes("highlight") || value.includes("catalog")) return "Ищем лучшие моменты";
    return "Продолжаем обработку";
  };

  const id = root.dataset.publicId;
  const elements = {
    status: root.querySelector("#status"),
    stage: root.querySelector("#generation-stage"),
    bar: root.querySelector("#bar"),
    progress: root.querySelector("#progress-value"),
    demos: root.querySelector("#demo-count"),
    players: root.querySelector("#player-count"),
    highlights: root.querySelector("#highlight-count"),
    action: root.querySelector("#next-action"),
    progressView: root.querySelector("#generation-progress-view"),
    video: root.querySelector("#video-result"),
    error: root.querySelector("#generation-error"),
    events: root.querySelector("#event-feed")
  };
  const stageCards = [...root.querySelectorAll("[data-stage-key]")];

  const actionLabel = status =>
    status === "AwaitingPlayerSelection"
      ? "Выбрать игрока"
      : status === "AwaitingHighlightSelection"
        ? "Выбрать моменты"
        : "Добавить музыку";

  function render(state) {
    const label = labels[state.status] || "Обрабатываем";
    elements.status.textContent = label;
    elements.stage.textContent = label;
    elements.bar.style.width = `${state.progressPercent}%`;
    elements.bar.parentElement.setAttribute("aria-valuenow", state.progressPercent);
    elements.progress.textContent = state.progressPercent;
    elements.demos.textContent = state.demoCount;
    elements.players.textContent = state.playerCount;
    elements.highlights.textContent = state.highlightCount;
    for (const card of stageCards) {
      const stage = (state.stages || []).find(item => item.key === card.dataset.stageKey);
      if (!stage) continue;
      card.classList.remove("stage-state--pending", "stage-state--current", "stage-state--complete", "stage-state--failed", "stage-state--skipped");
      card.classList.add("stage-state--" + stage.state);
      if (stage.state === "current") card.setAttribute("aria-current", "step");
      else card.removeAttribute("aria-current");
      const title = card.querySelector("strong");
      if (title && stage.label) title.textContent = stage.label;
    }

    elements.action.replaceChildren();
    if (state.actionUrl) {
      const link = document.createElement("a");
      link.className = "btn btn-primary";
      link.href = state.actionUrl;
      link.textContent = actionLabel(state.status);
      elements.action.append(link);
    }

    if (state.completed) {
      const wasHidden = elements.video.hidden;
      elements.video.hidden = false;
      elements.progressView.hidden = true;
      if (wasHidden) showToast("Ваш мувик готов", "success", 6000);
    }
    if (state.errorCode) {
      elements.error.hidden = false;
      elements.error.querySelector("[data-error-message]").textContent =
        state.errorMessage || "Не удалось завершить обработку. Попробуйте ещё раз.";
      elements.error.querySelector("[data-error-reference]").textContent =
        state.errorCode;
    } else {
      elements.error.hidden = true;
    }

    elements.events.replaceChildren();
    for (const item of state.events || []) {
      const line = document.createElement("p");
      line.textContent = `${item.progressPercent}% · ${eventLabel(item.message)}`;
      line.dataset.technicalMessage = item.message;
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
      showConnectionIssue();
      return false;
    }
  }

  let timer;
  async function tick() {
    if (await refresh()) window.clearInterval(timer);
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
      elements.status.textContent = labels[progress.status] || "Обрабатываем";
      elements.stage.textContent = labels[progress.status] || "Обрабатываем";
      elements.bar.style.width = `${progress.progressPercent}%`;
      elements.progress.textContent = progress.progressPercent;
      void refresh();
    });
    connection.onreconnecting(showConnectionIssue);
    connection.onreconnected(() => {
      showConnectionRestored();
      void connection.invoke("Subscribe", id);
      void refresh();
    });
    connection.onclose(showConnectionIssue);
    connection.start()
      .then(() => connection.invoke("Subscribe", id))
      .catch(showConnectionIssue);
  }
}
