using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Previews;

public enum PreviewRenderOutcome { Ready, Unsupported, Failed }

/// <summary>Один готовый артефакт превью — то, что дальше AttachmentPreviewProcessor шифрует и
/// кладёт в хранилище под StorageKeyFactory.CreatePreviewKey, записывая строку в AttachmentPreviews.</summary>
public record RenderedPreviewArtifact(
    AttachmentPreviewKind Kind, byte[] Bytes, string ContentType, int? Width, int? Height, int? PageCount);

public record PreviewRenderResult(PreviewRenderOutcome Outcome, IReadOnlyList<RenderedPreviewArtifact> Artifacts, string? FailureReason)
{
    public static readonly PreviewRenderResult NoArtifacts = new(PreviewRenderOutcome.Unsupported, [], null);
}
