namespace FamilyHub.Api.Features.Push;

public record VapidPublicKeyResponse(string PublicKey);

/// <summary>Зеркалит PushSubscriptionJSON браузера (SwPush.requestSubscription() на фронте).</summary>
public record SubscribePushRequest(string Endpoint, string P256dh, string Auth);

public record UnsubscribePushRequest(string Endpoint);
