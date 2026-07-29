using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Ui;

public static class UiText
{
    public static string Status(GenerationStatus status) => Status(status.ToString());

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
        "AwaitingMovieConfiguration" => "Настройте мувик",
        "ValidatingMoviePlan" => "Проверяем настройки",
        "AwaitingPayment" => "Ожидаем оплату",
        "PaymentProcessing" => "Проверяем оплату",
        "Paid" => "Оплачено",
        "QueuedForGeneration" => "Мувик в очереди",
        "PreparingRenderPlan" => "Готовим монтаж",
        "SelectingHighlights" => "Собираем моменты",
        "RenderingClips" => "Рендерим моменты",
        "VerifyingClips" => "Проверяем клипы",
        "PlanningMusicEdit" => "Подстраиваем монтаж под трек",
        "ApplyingTimeWarp" => "Подстраиваем моменты под ритм",
        "ApplyingEffects" => "Добавляем эффекты",
        "ComposingVideo" => "Собираем финальный монтаж",
        "MixingAudio" => "Смешиваем звук",
        "ApplyingColorGrade" => "Применяем цвет",
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
        GenerationStatus.AwaitingMovieConfiguration or
        GenerationStatus.ValidatingMoviePlan => 4,
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

    public static string MovieStyle(MovieStyle style) => style switch
    {
        Cs2Highlight.Music.MovieStyle.Clean => "Чистый",
        Cs2Highlight.Music.MovieStyle.Dynamic => "Динамичный",
        Cs2Highlight.Music.MovieStyle.Cinematic => "Кинематографичный",
        _ => "Динамичный"
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
