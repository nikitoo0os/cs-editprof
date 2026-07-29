namespace Cs2Highlight.Web.Ui;

public sealed record UiStatCard(
    string Label,
    string Value,
    string Icon,
    string? ValueId = null);

public sealed record UiState(
    string Icon,
    string Title,
    string Description,
    string? ActionText = null,
    string? ActionUrl = null,
    string? Reference = null);

public sealed record UploadDropzoneViewModel(
    int MaximumFiles,
    long MaximumFileSizeBytes);

public sealed record VideoPlayerViewModel(
    string PublicId,
    string AccessibleTitle);
