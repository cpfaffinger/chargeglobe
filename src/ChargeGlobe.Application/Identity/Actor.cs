namespace ChargeGlobe.Application.Identity;

/// <summary>Who performs a writing action; recorded in the audit log.</summary>
/// <param name="UserId">Acting user; for an API key its owner.</param>
/// <param name="ApiKeyId">Set if the request was authenticated with an API key.</param>
public sealed record Actor(Guid? UserId, Guid? ApiKeyId = null);
