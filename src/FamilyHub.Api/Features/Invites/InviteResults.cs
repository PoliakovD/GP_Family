namespace FamilyHub.Api.Features.Invites;

public enum CreateInviteResult { Created, Forbidden }

public enum RevokeInviteResult { Revoked, Forbidden, NotFound }

public enum ApproveRejectResult { Success, Forbidden, NotFound }
