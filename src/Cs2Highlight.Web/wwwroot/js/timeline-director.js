const root = document.querySelector("[data-timeline-director]");

if (root) {
  const generationId = root.dataset.generationId;
  const endpoint = `/api/generations/${encodeURIComponent(generationId)}/timeline`;
  const canvas = root.querySelector("[data-timeline-canvas]");
  const scroll = root.querySelector("[data-timeline-scroll]");
  const waveform = root.querySelector("[data-timeline-waveform]");
  const sectionTrack = root.querySelector("[data-timeline-sections]");
  const peakTrack = root.querySelector("[data-timeline-peaks]");
  const anchorTrack = root.querySelector("[data-timeline-anchors]");
  const gapTrack = root.querySelector("[data-timeline-gaps]");
  const validation = root.querySelector("[data-timeline-validation]");
  const inspector = root.querySelector("[data-timeline-inspector]");
  const playhead = root.querySelector("[data-timeline-playhead]");
  const timeOutput = root.querySelector("[data-timeline-time]");
  const snapGuide = root.querySelector("[data-timeline-snap-guide]");
  const audio = new Audio(`/generations/${encodeURIComponent(generationId)}/music-audio`);

  let state;
  let selectedAnchorId = null;
  let selectedRegionId = null;
  let draggedHighlightId = null;
  let pointerDrag = null;
  let optimisticTime = null;
  let previewEndTime = null;

  const statusLabels = {
    natural: "Natural",
    acceptable: "Retiming",
    risky: "Risky",
    invalid: "Invalid"
  };

  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const percent = seconds => `${(seconds / state.durationSeconds) * 100}%`;
  const formatTime = seconds => {
    const value = Math.max(0, seconds);
    const minutes = Math.floor(value / 60);
    const remainder = value - minutes * 60;
    return `${String(minutes).padStart(2, "0")}:${remainder.toFixed(3).padStart(6, "0")}`;
  };

  function escapeHtml(value) {
    const element = document.createElement("span");
    element.textContent = value ?? "";
    return element.innerHTML;
  }

  function announce(message, kind = "neutral") {
    validation.textContent = message;
    validation.dataset.kind = kind;
  }

  async function api(path = "", options = {}) {
    const response = await fetch(`${endpoint}${path}`, {
      headers: { "Content-Type": "application/json", ...(options.headers || {}) },
      ...options
    });
    if (response.status === 409) {
      const conflict = await response.json();
      announce("План изменился в другой вкладке. Загружаю актуальную ревизию.", "warning");
      await load();
      throw new Error(conflict.error || "TIMELINE_REVISION_CONFLICT");
    }
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      const message = problem.error
        || problem.errors?.timeline?.[0]
        || "Не удалось сохранить изменение";
      announce(message, "error");
      throw new Error(message);
    }
    return response.json();
  }

  async function load() {
    state = await api();
    render();
  }

  async function mutate(path, method, body) {
    const previous = state;
    const anchorMatch = path.match(/\/anchors\/([^?]+)/);
    if (anchorMatch) {
      const anchorId = decodeURIComponent(anchorMatch[1]);
      state.gaps
        .filter(gap => gap.previousAnchorId === anchorId || gap.nextAnchorId === anchorId)
        .forEach(gap => {
          const element = gapTrack.querySelector(`[data-region-id="${CSS.escape(gap.id)}"]`);
          if (element) element.classList.add("is-replanning");
        });
      announce("Replanning region", "neutral");
    }
    try {
      state = await api(path, {
        method,
        body: body === undefined ? undefined : JSON.stringify({
          ...body,
          concurrencyToken: state.concurrencyToken
        })
      });
      render(previous);
      return true;
    } catch {
      state = state || previous;
      render();
      return false;
    }
  }

  function renderWaveform() {
    const pixelWidth = Math.round(waveform.getBoundingClientRect().width * Math.max(1, window.devicePixelRatio || 1));
    const key = `${state.waveform.schemaVersion}:${state.waveform.available}:${state.waveform.peaks.length}:${state.durationSeconds}:${pixelWidth}`;
    if (waveform.dataset.renderKey === key) return;
    waveform.dataset.renderKey = key;
    waveform.replaceChildren();
    waveform.classList.toggle("is-unavailable", !state.waveform.available);
    if (!state.waveform.available || !state.waveform.peaks.length) {
      const message = document.createElement("span");
      message.className = "timeline-waveform__unavailable";
      message.textContent = "Waveform unavailable · аудио доступно";
      waveform.append(message);
      announce("Waveform не построена. Показывать декоративную форму запрещено; воспроизведение остаётся доступным.", "warning");
      return;
    }
    const drawing = document.createElement("canvas");
    drawing.className = "timeline-waveform__canvas";
    drawing.setAttribute("aria-hidden", "true");
    waveform.append(drawing);
    const bounds = waveform.getBoundingClientRect();
    const ratio = Math.max(1, window.devicePixelRatio || 1);
    const width = Math.max(1, Math.round(bounds.width * ratio));
    const height = Math.max(1, Math.round(bounds.height * ratio));
    drawing.width = width;
    drawing.height = height;
    const context = drawing.getContext("2d");
    const center = height / 2;
    context.clearRect(0, 0, width, height);
    context.strokeStyle = "rgba(103, 232, 249, 0.78)";
    context.lineWidth = Math.max(1, ratio);
    context.beginPath();
    for (const peak of state.waveform.peaks) {
      const x = clamp(peak.timeSeconds / state.durationSeconds, 0, 1) * width;
      context.moveTo(x, center - clamp(peak.max, 0, 1) * center);
      context.lineTo(x, center + clamp(peak.min, 0, 1) * center);
    }
    context.stroke();
  }

  function renderRuler() {
    const ruler = root.querySelector("[data-timeline-ruler]");
    const interval = state.durationSeconds <= 20 ? 2 : 5;
    const ticks = [];
    for (let time = 0; time <= state.durationSeconds + 0.001; time += interval) {
      ticks.push(`<span style="left:${percent(time)}">${Math.round(time)}s</span>`);
    }
    ruler.innerHTML = ticks.join("");
  }

  function renderSections() {
    sectionTrack.innerHTML = state.sections.map(section => `
      <span class="timeline-section timeline-section--${section.type.toLowerCase()}"
            style="left:${percent(section.startSeconds)};width:${percent(section.endSeconds - section.startSeconds)}"
            title="${escapeHtml(section.type)} ${formatTime(section.startSeconds)}">
        ${escapeHtml(section.type)}
      </span>`).join("");
  }

  function renderPeaks() {
    peakTrack.innerHTML = state.snapPoints
      .filter(point => point.strength >= 0.42)
      .map(point => `
        <i class="timeline-peak timeline-peak--${point.type.toLowerCase()}"
           style="left:${percent(point.timeSeconds)};--peak-strength:${clamp(point.strength, 0.2, 1)}"
           title="${escapeHtml(point.type)} · ${formatTime(point.timeSeconds)}"></i>`)
      .join("");
  }

  function anchorTitle(anchor) {
    const highlight = state.highlights.find(item => item.id === anchor.highlightId);
    if (highlight) return highlight.type.replace("Kill", " Kill");
    return anchor.markerType.replace(/^Best/, "Best ");
  }

  function renderAnchors() {
    anchorTrack.innerHTML = state.anchors.map((anchor, index) => {
      const selected = anchor.id === selectedAnchorId ? " is-selected" : "";
      const locked = anchor.isLocked ? " is-locked" : "";
      const status = anchor.feasibility.toLowerCase();
      const target = optimisticTime?.id === anchor.id
        ? optimisticTime.time
        : anchor.targetMusicTimeSeconds;
      return `
        <button type="button"
                class="timeline-anchor timeline-anchor--${status}${selected}${locked}"
                style="left:${percent(target)}"
                data-anchor-id="${escapeHtml(anchor.id)}"
                aria-label="${escapeHtml(anchorTitle(anchor))}, ${formatTime(target)}, ${statusLabels[status]}"
                aria-describedby="timeline-anchor-status-${index}"
                ${state.isLocked ? "disabled" : ""}>
          <span class="timeline-anchor__pin" aria-hidden="true"></span>
          <span class="timeline-anchor__label">${escapeHtml(anchorTitle(anchor))}</span>
          <span class="timeline-anchor__status" id="timeline-anchor-status-${index}">${statusLabels[status]}</span>
          ${anchor.isLocked ? '<span class="timeline-anchor__lock" aria-hidden="true">⌾</span>' : ""}
        </button>`;
    }).join("");
  }

  function renderGaps(previousGaps = null) {
    const previous = new Map((previousGaps || []).map(gap => [gap.id, JSON.stringify(gap)]));
    const retained = new Set();
    for (const gap of state.gaps) {
      retained.add(gap.id);
      let element = gapTrack.querySelector(`[data-region-id="${CSS.escape(gap.id)}"]`);
      if (element && previous.get(gap.id) === JSON.stringify(gap)) continue;
      if (!element) {
        element = document.createElement("button");
        element.type = "button";
        gapTrack.append(element);
      }
      element.className = `timeline-gap timeline-gap--${gap.role.toLowerCase()} timeline-gap--${gap.outcome.toLowerCase()}`;
      element.dataset.regionId = gap.id;
      element.style.left = percent(gap.startSeconds);
      element.style.width = percent(gap.endSeconds - gap.startSeconds);
      element.title = `${gap.role} · ${gap.camera} · ${gap.material} · ${gap.cameraVerification}`;
      element.innerHTML = `
        <strong>${escapeHtml(gap.role)}</strong>
        <small>${escapeHtml(gap.camera)} · ${escapeHtml(gap.outcome)}</small>
        <em>${gap.reused ? "Reused" : "Unique"}${gap.cameraFallback ? " · fallback" : ""}</em>`;
    }
    gapTrack.querySelectorAll("[data-region-id]").forEach(element => {
      if (!retained.has(element.dataset.regionId)) element.remove();
    });
  }

  function renderInspector() {
    const region = state.gaps.find(item => item.id === selectedRegionId);
    if (region) {
      inspector.innerHTML = `
        <div class="timeline-inspector-content">
          <div class="timeline-inspector-title">
            <div><strong>${escapeHtml(region.role)}</strong><span>${formatTime(region.startSeconds)}–${formatTime(region.endSeconds)}</span></div>
            <span class="timeline-status timeline-status--${region.outcome.toLowerCase()}">${escapeHtml(region.outcome)}</span>
          </div>
          <dl class="timeline-inspector-grid">
            <div><dt>Camera</dt><dd>${escapeHtml(region.camera)}</dd></div>
            <div><dt>Material</dt><dd>${escapeHtml(region.material)}</dd></div>
            <div><dt>Source</dt><dd>${region.reused ? "Reused plan" : "Unique interval"}</dd></div>
            <div><dt>Verification</dt><dd>${escapeHtml(region.cameraVerification)}</dd></div>
          </dl>
          <div class="timeline-inspector-actions">
            <button type="button" class="btn btn-ghost" data-region-preview="${escapeHtml(region.id)}">Preview region</button>
          </div>
          <div class="timeline-region-preview" data-region-preview-media></div>
        </div>`;
      return;
    }
    const anchor = state.anchors.find(item => item.id === selectedAnchorId);
    if (!anchor) {
      inspector.innerHTML = `
        <div class="timeline-inspector-empty">
          <strong>Выберите маркер</strong>
          <p>Здесь появятся retiming, pre-roll, SafeEnd и доступные действия.</p>
        </div>`;
      return;
    }
    const highlight = state.highlights.find(item => item.id === anchor.highlightId);
    const status = anchor.feasibility.toLowerCase();
    inspector.innerHTML = `
      <div class="timeline-inspector-content">
        <div class="timeline-inspector-title">
          <div>
            <strong>${escapeHtml(anchorTitle(anchor))}</strong>
            <span>${formatTime(anchor.targetMusicTimeSeconds)}</span>
          </div>
          <span class="timeline-status timeline-status--${status}">${statusLabels[status]}</span>
        </div>
        <dl class="timeline-inspector-grid">
          <div><dt>Required speed</dt><dd>${anchor.requiredBaseSpeed.toFixed(2)}x</dd></div>
          <div><dt>Local speed</dt><dd>${anchor.requiredLocalSpeed.toFixed(2)}x</dd></div>
          <div><dt>Pre-roll</dt><dd>${anchor.estimatedPreRollSeconds.toFixed(2)} sec</dd></div>
          <div><dt>Post-kill</dt><dd>${anchor.estimatedPostRollSeconds.toFixed(2)} sec</dd></div>
          ${highlight ? `<div><dt>Highlight</dt><dd>${escapeHtml(highlight.mapName)} · R${highlight.roundNumber}</dd></div>` : ""}
          ${highlight ? `<div><dt>Primary kill</dt><dd>${highlight.primaryKillOffsetSeconds.toFixed(2)} sec after start</dd></div>` : ""}
        </dl>
        ${anchor.warnings.length ? `
          <div class="timeline-warning-list">
            ${anchor.warnings.map(item => `<span>${escapeHtml(item.replaceAll("_", " "))}</span>`).join("")}
          </div>` : ""}
        <label class="form-label" for="timeline-marker-type">Тип маркера</label>
        <select class="form-select" id="timeline-marker-type" data-inspector-type ${anchor.isLocked ? "disabled" : ""}>
          ${["ExactHighlight", "BestSolo", "BestDouble", "BestTriple", "BestQuad", "BestAce", "BestAvailableHighlight"]
            .map(value => `<option value="${value}" ${value === anchor.markerType ? "selected" : ""}>${value.replace(/^Best/, "Best ")}</option>`)
            .join("")}
        </select>
        <div class="timeline-inspector-actions">
          <button type="button" class="btn btn-secondary" data-inspector-lock>${anchor.isLocked ? "Разблокировать" : "Зафиксировать"}</button>
          <button type="button" class="btn btn-ghost" data-inspector-preview>Прослушать</button>
          <button type="button" class="btn btn-danger" data-inspector-delete ${anchor.isLocked ? "disabled" : ""}>Удалить</button>
        </div>
      </div>`;
  }

  function renderSummary() {
    const counts = state.anchors.reduce((result, anchor) => {
      result[anchor.feasibility.toLowerCase()] += 1;
      return result;
    }, { natural: 0, acceptable: 0, risky: 0, invalid: 0 });
    root.querySelector("[data-timeline-summary]").textContent =
      `${state.anchors.length} маркеров · ${counts.natural} natural · ${counts.invalid} invalid`;
    root.querySelector("[data-timeline-revision]").textContent = state.revision;
    root.querySelectorAll("[data-timeline-mode]").forEach(button => {
      const active = button.dataset.timelineMode === state.mode;
      button.classList.toggle("is-active", active);
      button.setAttribute("aria-checked", String(active));
      button.disabled = state.isLocked;
    });
    root.querySelector("[data-timeline-confirm]").disabled =
      state.isLocked || counts.invalid > 0 || state.anchors.length === 0;
    if (state.isLocked) {
      announce("Timeline locked for rendering.", "success");
    } else if (counts.invalid > 0) {
      announce(`${counts.invalid} маркер(а) требуют исправления. Продолжение заблокировано.`, "error");
    } else {
      announce("Расстановка выполнима. SafeEnd и post-kill сохранены.", "success");
    }
  }

  function render(previous = null) {
    renderWaveform();
    renderRuler();
    renderSections();
    renderPeaks();
    renderAnchors();
    renderGaps(previous?.gaps);
    renderInspector();
    renderSummary();
  }

  function nearestSnap(raw, disabled = false) {
    if (disabled || !state.snapPoints.length) return { time: raw, point: null };
    const threshold = Math.max(0.16, state.durationSeconds / 180);
    const point = state.snapPoints
      .map(item => ({ item, distance: Math.abs(item.timeSeconds - raw) }))
      .filter(item => item.distance <= threshold)
      .sort((left, right) =>
        (right.item.strength - left.item.strength)
        || (left.distance - right.distance))[0]?.item;
    return { time: point?.timeSeconds ?? raw, point: point ?? null };
  }

  function timeFromPointer(event) {
    const rect = anchorTrack.getBoundingClientRect();
    return clamp(
      ((event.clientX - rect.left) / rect.width) * state.durationSeconds,
      0,
      state.durationSeconds);
  }

  function showSnap(point) {
    if (!point) {
      snapGuide.hidden = true;
      return;
    }
    snapGuide.hidden = false;
    snapGuide.style.left = percent(point.timeSeconds);
    snapGuide.textContent = `${point.type.replaceAll(/([A-Z])/g, " $1").trim()} · ${formatTime(point.timeSeconds)}`;
  }

  anchorTrack.addEventListener("pointerdown", event => {
    const marker = event.target.closest("[data-anchor-id]");
    if (!marker || state.isLocked) return;
    const anchor = state.anchors.find(item => item.id === marker.dataset.anchorId);
    selectedAnchorId = anchor.id;
    selectedRegionId = null;
    renderInspector();
    if (anchor.isLocked) return;
    marker.setPointerCapture(event.pointerId);
    pointerDrag = {
      id: anchor.id,
      pointerId: event.pointerId,
      originalTime: anchor.targetMusicTimeSeconds
    };
    optimisticTime = { id: anchor.id, time: anchor.targetMusicTimeSeconds };
    event.preventDefault();
  });

  anchorTrack.addEventListener("pointermove", event => {
    if (!pointerDrag || event.pointerId !== pointerDrag.pointerId) return;
    const snapped = nearestSnap(timeFromPointer(event), event.altKey);
    optimisticTime = { id: pointerDrag.id, time: snapped.time };
    showSnap(snapped.point);
    renderAnchors();
    timeOutput.textContent = formatTime(snapped.time);
  });

  anchorTrack.addEventListener("pointerup", async event => {
    if (!pointerDrag || event.pointerId !== pointerDrag.pointerId) return;
    const { id, originalTime } = pointerDrag;
    const target = optimisticTime.time;
    pointerDrag = null;
    optimisticTime = null;
    showSnap(null);
    if (Math.abs(target - originalTime) < 0.0005) {
      renderAnchors();
      return;
    }
    await mutate(`/anchors/${encodeURIComponent(id)}`, "PUT", {
      targetMusicTimeSeconds: target
    });
  });

  anchorTrack.addEventListener("click", event => {
    const marker = event.target.closest("[data-anchor-id]");
    if (!marker) return;
    selectedAnchorId = marker.dataset.anchorId;
    selectedRegionId = null;
    renderAnchors();
    renderInspector();
  });

  gapTrack.addEventListener("click", event => {
    const region = event.target.closest("[data-region-id]");
    if (!region) return;
    selectedRegionId = region.dataset.regionId;
    selectedAnchorId = null;
    renderAnchors();
    renderInspector();
  });

  root.addEventListener("dragstart", event => {
    const card = event.target.closest("[data-highlight-id]");
    if (!card) return;
    draggedHighlightId = card.dataset.highlightId;
    event.dataTransfer.effectAllowed = "copy";
    event.dataTransfer.setData("text/plain", draggedHighlightId);
  });

  root.querySelector("[data-timeline-dropzone]").addEventListener("dragover", event => {
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
  });

  root.querySelector("[data-timeline-dropzone]").addEventListener("drop", async event => {
    event.preventDefault();
    const id = draggedHighlightId || event.dataTransfer.getData("text/plain");
    if (!id) return;
    const raw = timeFromPointer(event);
    const snapped = nearestSnap(raw, event.altKey);
    await mutate("/anchors", "POST", {
      markerType: "ExactHighlight",
      highlightId: id,
      targetMusicTimeSeconds: snapped.time
    });
    selectedAnchorId = state.anchors.at(-1)?.id;
    render();
  });

  root.addEventListener("click", async event => {
    const add = event.target.closest("[data-add-highlight]");
    if (add) {
      const raw = audio.currentTime || state.durationSeconds / 2;
      const snapped = nearestSnap(raw);
      await mutate("/anchors", "POST", {
        markerType: "ExactHighlight",
        highlightId: add.dataset.addHighlight,
        targetMusicTimeSeconds: snapped.time
      });
      selectedAnchorId = state.anchors.at(-1)?.id;
      render();
      return;
    }
    const category = event.target.closest("[data-add-category]");
    if (category) {
      await mutate("/anchors", "POST", {
        markerType: category.dataset.addCategory,
        highlightId: null,
        targetMusicTimeSeconds: nearestSnap(state.durationSeconds * 0.72).time
      });
      return;
    }
    const mode = event.target.closest("[data-timeline-mode]");
    if (mode) {
      await mutate("/mode", "PUT", { mode: mode.dataset.timelineMode });
      return;
    }
    if (event.target.closest("[data-timeline-suggest]")) {
      await mutate("/suggest", "POST", {});
      return;
    }
    if (event.target.closest("[data-timeline-undo]")) {
      await mutate("/undo", "POST", {});
      return;
    }
    if (event.target.closest("[data-timeline-redo]")) {
      await mutate("/redo", "POST", {});
      return;
    }
    if (event.target.closest("[data-inspector-lock]")) {
      const anchor = state.anchors.find(item => item.id === selectedAnchorId);
      await mutate(`/anchors/${encodeURIComponent(anchor.id)}`, "PUT", {
        isLocked: !anchor.isLocked
      });
      return;
    }
    if (event.target.closest("[data-inspector-delete]")) {
      await mutate(`/anchors/${encodeURIComponent(selectedAnchorId)}?concurrencyToken=${encodeURIComponent(state.concurrencyToken)}`, "DELETE");
      selectedAnchorId = null;
      render();
      return;
    }
    if (event.target.closest("[data-inspector-preview]")) {
      const anchor = state.anchors.find(item => item.id === selectedAnchorId);
      audio.currentTime = state.waveform.excerptStartSeconds + anchor.targetMusicTimeSeconds;
      previewEndTime = state.waveform.excerptStartSeconds + Math.min(
        state.durationSeconds,
        anchor.targetMusicTimeSeconds + 3);
      await audio.play().catch(() => announce("Audio preview недоступен", "warning"));
      return;
    }
    const regionPreview = event.target.closest("[data-region-preview]");
    if (regionPreview) {
      const preview = await api(`/regions/${encodeURIComponent(regionPreview.dataset.regionPreview)}/preview`);
      previewEndTime = state.waveform.excerptStartSeconds + preview.endSeconds;
      audio.currentTime = state.waveform.excerptStartSeconds + preview.startSeconds;
      const media = inspector.querySelector("[data-region-preview-media]");
      if (preview.cameraPreviewUrl) {
        media.innerHTML = `<video controls playsinline src="${escapeHtml(preview.cameraPreviewUrl)}"></video>`;
      } else {
        media.textContent = `${preview.audioMix} · camera preview unavailable`;
      }
      await audio.play().catch(() => announce("Audio preview unavailable", "warning"));
      return;
    }
    if (event.target.closest("[data-timeline-play]")) {
      if (audio.paused) {
        const excerptStart = state.waveform.excerptStartSeconds;
        const excerptEnd = excerptStart + state.durationSeconds;
        if (audio.currentTime < excerptStart || audio.currentTime >= excerptEnd) audio.currentTime = excerptStart;
        previewEndTime = null;
        await audio.play().catch(() => announce("Audio preview недоступен", "warning"));
      }
      else audio.pause();
      return;
    }
    if (event.target.closest("[data-timeline-help]")) {
      const panel = root.querySelector("[data-timeline-help-panel]");
      panel.hidden = !panel.hidden;
      return;
    }
    if (event.target.closest("[data-timeline-confirm]")) {
      const success = await mutate("/confirm", "POST", {});
      if (success) window.location.assign(`/generations/${encodeURIComponent(generationId)}/checkout`);
    }
  });

  root.addEventListener("change", async event => {
    if (event.target.matches("[data-inspector-type]")) {
      await mutate(`/anchors/${encodeURIComponent(selectedAnchorId)}`, "PUT", {
        markerType: event.target.value
      });
    }
    if (event.target.matches("[data-timeline-zoom]")) {
      const playheadRatio = (audio.currentTime - state.waveform.excerptStartSeconds) / state.durationSeconds;
      canvas.style.setProperty("--timeline-zoom", event.target.value);
      requestAnimationFrame(() => {
        scroll.scrollLeft = Math.max(
          0,
          playheadRatio * canvas.scrollWidth - scroll.clientWidth / 2);
      });
    }
  });

  root.querySelectorAll("[data-highlight-filter]").forEach(button => {
    button.addEventListener("click", () => {
      const filter = button.dataset.highlightFilter;
      button.classList.toggle("is-active");
      root.querySelectorAll("[data-highlight-id]").forEach(card => {
        card.hidden = button.classList.contains("is-active")
          ? !card.dataset.highlightType.toLowerCase().includes(filter.toLowerCase())
          : false;
      });
    });
  });

  canvas.addEventListener("click", event => {
    if (event.target.closest("[data-anchor-id]")) return;
    const rect = canvas.getBoundingClientRect();
    audio.currentTime = state.waveform.excerptStartSeconds + clamp(
      ((event.clientX - rect.left) / rect.width) * state.durationSeconds,
      0,
      state.durationSeconds);
    updatePlayhead();
  });

  root.addEventListener("keydown", async event => {
    const anchor = state.anchors.find(item => item.id === selectedAnchorId);
    if (event.code === "Space" && !event.target.matches("input,select,textarea")) {
      event.preventDefault();
      if (audio.paused) await audio.play().catch(() => {});
      else audio.pause();
      return;
    }
    if (!anchor || anchor.isLocked || event.target.matches("input,select,textarea")) return;
    if (event.key === "Delete") {
      event.preventDefault();
      await mutate(`/anchors/${encodeURIComponent(anchor.id)}?concurrencyToken=${encodeURIComponent(state.concurrencyToken)}`, "DELETE");
      selectedAnchorId = null;
      return;
    }
    if (event.key.toLowerCase() === "l") {
      event.preventDefault();
      await mutate(`/anchors/${encodeURIComponent(anchor.id)}`, "PUT", { isLocked: true });
      return;
    }
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      const direction = event.key === "ArrowLeft" ? -1 : 1;
      const step = event.shiftKey ? 0.01 : 0.1;
      const raw = clamp(anchor.targetMusicTimeSeconds + direction * step, 0, state.durationSeconds);
      const snapped = nearestSnap(raw, event.altKey || event.shiftKey).time;
      const target = Math.abs(snapped - anchor.targetMusicTimeSeconds) < 0.0005
        ? raw
        : snapped;
      await mutate(`/anchors/${encodeURIComponent(anchor.id)}`, "PUT", {
        targetMusicTimeSeconds: target
      });
    }
  });

  function updatePlayhead() {
    if (!state) return;
    const excerptEnd = state.waveform.excerptStartSeconds + state.durationSeconds;
    if (!audio.paused && (audio.currentTime >= excerptEnd ||
      (previewEndTime && audio.currentTime >= previewEndTime))) {
      audio.pause();
      previewEndTime = null;
    }
    const current = clamp(
      audio.currentTime - state.waveform.excerptStartSeconds,
      0,
      state.durationSeconds);
    playhead.style.left = percent(current);
    timeOutput.textContent = formatTime(current);
    root.querySelector("[data-timeline-play]").textContent = audio.paused ? "▶" : "❚❚";
  }

  audio.addEventListener("timeupdate", updatePlayhead);
  audio.addEventListener("play", updatePlayhead);
  audio.addEventListener("pause", updatePlayhead);

  new ResizeObserver(() => {
    if (!state) return;
    delete waveform.dataset.renderKey;
    renderWaveform();
  }).observe(waveform);

  load().catch(error => announce(error.message, "error"));
}
