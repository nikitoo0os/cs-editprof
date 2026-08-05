using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Ui;

public static class UiText
{
    public static string HighlightRemovalRequired(
        int selectedCount,
        int requiredRemovalCount)
    {
        int remove = Math.Max(1, requiredRemovalCount);
        int remaining = Math.Max(0, selectedCount - remove);
        return $"Чтобы монтаж поместился в трек, оставьте не больше {remaining} из {selectedCount} выбранных моментов — уберите минимум {remove}.";
    }

    public static string Error(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "Не удалось выполнить операцию. Попробуйте ещё раз.";

        if (code.StartsWith("MUSIC_ANALYSIS_FAILED", StringComparison.Ordinal))
        {
            return "Не удалось проанализировать трек. Проверьте аудиофайл или загрузите другой.";
        }

        return code switch
        {
            "TOKEN_BALANCE_INSUFFICIENT" =>
                "На балансе нет свободного токена. Пополните баланс или дождитесь завершения уже запущенного видео.",
            "MUSIC_FILE_REQUIRED" => "Выберите музыкальный файл.",
            "MUSIC_RIGHTS_CONFIRMATION_REQUIRED" =>
                "Подтвердите право использовать этот трек.",
            "MUSIC_FILE_EMPTY" => "Выбранный музыкальный файл пуст.",
            "MUSIC_FILE_TOO_LARGE" =>
                "Музыкальный файл превышает допустимый размер.",
            "MUSIC_UNSUPPORTED_FORMAT" =>
                "Формат трека не поддерживается. Используйте MP3, WAV, FLAC, M4A или AAC.",
            "MUSIC_ALREADY_UPLOADED" => "Этот трек уже загружен.",
            "MUSIC_TOO_SHORT" => "Трек слишком короткий для монтажа.",
            "MUSIC_TOO_LONG" => "Трек превышает допустимую длительность.",
            "MUSIC_TOO_SHORT_FOR_SELECTION" =>
                "Трек слишком короткий для выбранных моментов. Уберите часть хайлайтов или загрузите более длинный трек.",
            "MUSIC_FFPROBE_FAILED" or
            "MUSIC_DECODING_FAILED" or
            "MUSIC_NO_AUDIO_STREAM" =>
                "Не удалось прочитать аудиодорожку. Проверьте файл или загрузите другой трек.",
            "MUSIC_ANALYZER_START_FAILED" or
            "MUSIC_ANALYZER_TIMEOUT" or
            "MUSIC_ANALYSIS_INVALID" =>
                "Не удалось завершить анализ музыки. Попробуйте загрузить трек ещё раз.",
            "UNKNOWN_LUT_ASSET" or
            "UNTRUSTED_LUT_ASSET" or
            "LUT_ASSET_MISSING" =>
                "Выбранный цветовой профиль недоступен. Выберите другой вариант.",
            "CINEMATIC_HIGHLIGHTS_REQUIRED" =>
                "Для Cinematic Director нужно выбрать хотя бы один хайлайт.",
            "CINEMATIC_MUSIC_REANALYSIS_REQUIRED" =>
                "Для Cinematic Director нужно заново проанализировать трек.",
            "CINEMATIC_INSUFFICIENT_HIGH_ENERGY_PEAKS" =>
                "В треке недостаточно сильных музыкальных акцентов для всех выбранных моментов. Уберите часть хайлайтов или выберите другой трек.",
            "CINEMATIC_MUSIC_EXCERPT_UNAVAILABLE" =>
                "Не удалось подобрать подходящий фрагмент трека. Выберите другую длительность или другой трек.",
            "CINEMATIC_GAMEPLAY_TIMELINE_UNAVAILABLE_REANALYZE_DEMOS" =>
                "Для Cinematic Director не хватает данных о движении игроков. Заново проанализируйте демки.",
            "CINEMATIC_BROLL_INSUFFICIENT" or
            "CINEMATIC_BROLL_INSUFFICIENT_FOR_CONTIGUOUS_TIMELINE" =>
                "Не хватает безопасных игровых фрагментов, чтобы заполнить паузы без повторов. Выберите меньшую длительность или добавьте больше хайлайтов.",
            "CINEMATIC_LOCKED_PLAN_INVALID" =>
                "Сохранённый план монтажа повреждён. Настройте мувик заново.",
            "PRIMARY_KILL_OUTSIDE_HIGH_ENERGY_SECTION" =>
                "Не удалось совместить основные убийства с сильными частями трека. Выберите другой трек или меньше хайлайтов.",
            "CINEMATIC_DURATION_LIMIT_EXCEEDED" =>
                "План монтажа превышает выбранную длительность. Выберите более длинный вариант или меньше хайлайтов.",
            "NO_HIGHLIGHTS_SELECTED" => "Выберите хотя бы один хайлайт.",
            "GENERATION_SELECTION_LOCKED" =>
                "Выбор моментов уже зафиксирован для этой генерации.",
            "INVALID_HIGHLIGHT_SELECTION" =>
                "Не удалось сохранить выбранные моменты. Обновите страницу и попробуйте снова.",
            "INSUFFICIENT_DISK_SPACE" =>
                "На диске недостаточно свободного места для загрузки.",
            _ => "Не удалось выполнить операцию. Попробуйте ещё раз."
        };
    }

    public static string Status(GenerationStatus status) => Status(status.ToString());

    public static string Transaction(TokenTransactionType type) => type switch
    {
        TokenTransactionType.Purchase => "Покупка токенов",
        TokenTransactionType.GenerationDebit => "Монтаж",
        TokenTransactionType.GenerationRefund => "Возврат за монтаж",
        TokenTransactionType.ReferralReward => "Бонус за друга",
        TokenTransactionType.AdminAdjustment => "Корректировка",
        TokenTransactionType.Chargeback => "Отмена покупки",
        TokenTransactionType.Expiration => "Срок токенов истёк",
        _ => "Операция с токенами"
    };

    public static string Status(string? status) => status switch
    {
        "Draft" => "Подготовка",
        "Uploading" => "Загружаем демки",
        "Uploaded" => "Демки загружены",
        "QueuedForAnalysis" => "Ожидаем анализ",
        "Analyzing" => "Анализируем демки",
        "BuildingHighlightCatalog" => "Ищем лучшие убийства",
        "AwaitingPlayerSelection" => "Выберите игрока",
        "AwaitingHighlightSelection" => "Выберите моменты",
        "AwaitingMusicUpload" => "Добавьте музыку",
        "AnalyzingMusic" => "Анализируем музыку",
        "AnalyzingMusicStructure" => "Разбираем структуру трека",
        "AwaitingMovieConfiguration" => "Настройте мувик",
        "ValidatingMoviePlan" => "Проверяем настройки",
        "SelectingMusicExcerpt" => "Выбираем лучший фрагмент трека",
        "AnalyzingGameplayTimeline" => "Анализируем движение в демке",
        "DetectingBroll" => "Ищем игровые подъезды",
        "PlanningNarrative" => "Строим драматургию",
        "PlanningCameraShots" => "Планируем камеры",
        "AwaitingPayment" => "Ожидаем оплату",
        "PaymentProcessing" => "Проверяем оплату",
        "Paid" => "Оплачено",
        "QueuedForGeneration" => "Мувик в очереди",
        "PreparingRenderPlan" => "Готовим монтаж",
        "SelectingHighlights" => "Собираем моменты",
        "RenderingClips" => "Рендерим моменты",
        "RenderingHighlights" => "Рендерим основные highlights",
        "VerifyingClips" => "Проверяем клипы",
        "PlanningMusicEdit" => "Подстраиваем монтаж под трек",
        "ApplyingTimeWarp" => "Подстраиваем моменты под ритм",
        "ApplyingEffects" => "Добавляем эффекты",
        "ComposingVideo" => "Собираем финальный монтаж",
        "MixingAudio" => "Смешиваем звук",
        "ApplyingColorGrade" => "Применяем цвет",
        "SynchronizingPeaks" => "Синхронизируем убийства с пиками",
        "RenderingCameraPreviews" => "Рендерим превью камер",
        "ValidatingCameraShots" => "Проверяем траектории камер",
        "RenderingCinematicShots" => "Рендерим cinematic shots",
        "ComposingCinematicTimeline" => "Собираем cinematic timeline",
        "MixingNarrativeAudio" => "Смешиваем звук по структуре трека",
        "ApplyingNarrativeColor" => "Применяем цветовую драматургию",
        "VerifyingCinematicMovie" => "Проверяем финальный cinematic movie",
        "VerifyingOutput" => "Проверяем готовое видео",
        "Completed" => "Готово",
        "CompletedWithWarnings" => "Готово с замечаниями",
        "Cancelling" => "Отменяем",
        "Cancelled" => "Отменено",
        "Failed" => "Нужна помощь",
        "Expired" => "Результат удалён",
        _ => "Обрабатываем"
    };

    public static string Stage(string? stage) =>
        string.IsNullOrWhiteSpace(stage) ? "Подготавливаем следующий этап" : Status(stage);

    public static int Step(GenerationStatus status) => status switch
    {
        <= GenerationStatus.BuildingHighlightCatalog => 1,
        GenerationStatus.AwaitingPlayerSelection => 2,
        GenerationStatus.AwaitingHighlightSelection => 3,
        GenerationStatus.AwaitingMusicUpload or
        GenerationStatus.AnalyzingMusic or
        GenerationStatus.AnalyzingMusicStructure or
        GenerationStatus.AwaitingMovieConfiguration or
        GenerationStatus.ValidatingMoviePlan or
        GenerationStatus.SelectingMusicExcerpt or
        GenerationStatus.AnalyzingGameplayTimeline or
        GenerationStatus.DetectingBroll or
        GenerationStatus.PlanningNarrative or
        GenerationStatus.PlanningCameraShots => 4,
        GenerationStatus.AwaitingPayment or
        GenerationStatus.PaymentProcessing or
        GenerationStatus.Paid => 5,
        _ => 6
    };

    public static string HighlightType(string type) => type switch
    {
        "SoloKill" => "Сольное убийство",
        "DoubleKill" => "Дабл-килл",
        "TripleKill" => "Трипл-килл",
        "QuadKill" => "Квадро-килл",
        "Ace" => "Эйс",
        _ => "Яркий момент"
    };

    public static string HighlightTab(string type) => type switch
    {
        "All" => "Все",
        "SoloKill" => "Solo",
        "DoubleKill" => "Double",
        "TripleKill" => "Triple",
        "QuadKill" => "Quad",
        "Ace" => "Ace",
        "Recommended" => "Рекомендованные",
        _ => type
    };

    public static string Tag(string tag) => tag switch
    {
        "HEADSHOT" => "Хедшот",
        "HEADSHOT_STREAK" => "Серия хедшотов",
        "WALLBANG" => "Прострел",
        "ONE_TAP" => "Вантап",
        "KNIFE" => "Нож",
        "ZEUS" => "Zeus",
        "NO_SCOPE" => "Без прицела",
        "THROUGH_SMOKE" => "Через дым",
        "LOW_HP" => "Низкое здоровье",
        "LONG_DISTANCE" => "Дальняя дистанция",
        "ROUND_ENDING_KILL" => "Концовка раунда",
        "LAST_ENEMY" => "Последний противник",
        "WEAPON_SWAP" => "Смена оружия",
        "ROUND_WIN" => "Победа в раунде",
        "FAST_SEQUENCE" => "Быстрая серия",
        _ => "Особый момент"
    };

    public static string Effect(EffectPreset preset) => preset switch
    {
        EffectPreset.None => "Без эффектов",
        EffectPreset.Clean => "Чистый",
        EffectPreset.Dynamic => "Динамичный",
        _ => "Динамичный"
    };

    public static string EffectIntensity(EffectIntensity intensity) => intensity switch
    {
        Cs2Highlight.Web.Domain.EffectIntensity.Minimal => "Минимальная",
        Cs2Highlight.Web.Domain.EffectIntensity.Balanced => "Сбалансированная",
        Cs2Highlight.Web.Domain.EffectIntensity.Strong => "Сильная",
        _ => "Сбалансированная"
    };

    public static string MovieStyle(MovieStyle style) => style switch
    {
        Cs2Highlight.Music.MovieStyle.Clean => "Чистый",
        Cs2Highlight.Music.MovieStyle.Dynamic => "Динамичный",
        Cs2Highlight.Music.MovieStyle.Cinematic => "Кинематографичный",
        Cs2Highlight.Music.MovieStyle.Aggressive => "Агрессивный",
        Cs2Highlight.Music.MovieStyle.CinematicDirector => "Cinematic Director",
        _ => "Динамичный"
    };

    public static string CinematicEditIntensity(
        CinematicEditIntensity intensity) =>
        intensity switch
        {
            Cs2Highlight.Music.CinematicEditIntensity.Calm => "Спокойная",
            Cs2Highlight.Music.CinematicEditIntensity.Dynamic => "Динамичная",
            _ => "Сбалансированная"
        };

    public static string Sync(MusicSyncIntensity intensity) => intensity switch
    {
        MusicSyncIntensity.Soft => "Мягкая",
        MusicSyncIntensity.Expressive => "Выраженная",
        MusicSyncIntensity.Aggressive => "Агрессивная",
        _ => "Выраженная"
    };

    public static string Color(ColorGradePreset color) => color switch
    {
        ColorGradePreset.None => "Без обработки",
        ColorGradePreset.Natural => "Естественный",
        ColorGradePreset.Competitive => "Соревновательный",
        ColorGradePreset.CinematicCool => "Холодное кино",
        ColorGradePreset.CinematicWarm => "Тёплое кино",
        ColorGradePreset.HighContrast => "Высокий контраст",
        ColorGradePreset.Neon => "Неон",
        _ => "Естественный"
    };

    public static string Event(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Продолжаем обработку";
        string value = message.ToLowerInvariant();
        if (value.Contains("render")) return "Рендерим выбранные моменты";
        if (value.Contains("music") || value.Contains("audio")) return "Подстраиваем монтаж и звук под музыку";
        if (value.Contains("effect")) return "Добавляем видеоэффекты";
        if (value.Contains("color") || value.Contains("lut")) return "Настраиваем цвет";
        if (value.Contains("verif")) return "Проверяем результат";
        if (value.Contains("catalog") || value.Contains("highlight")) return "Ищем лучшие моменты";
        if (value.Contains("demo") || value.Contains("analy")) return "Анализируем матчи";
        if (value.Contains("complete")) return "Видео готово";
        return "Выполняем текущий этап";
    }
}
