namespace FamilyHub.Api.Features.Admin;

public record AdminSpecimenRenameRequest(string DisplayName);

/// <summary>Синонимы источника целиком (сырые написания — нормализуются на бэкенде).</summary>
public record AdminSpecimenAliasesRequest(List<string>? Aliases);

public record AdminChangeSpecimenRequest(Guid SpecimenKbId);
