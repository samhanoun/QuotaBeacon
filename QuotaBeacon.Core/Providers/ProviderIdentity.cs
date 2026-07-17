namespace QuotaBeacon.Core.Providers;

public static class ProviderIdentity
{
    public const int MaximumLength = 64;

    public static bool TryNormalize(string? providerId, out string normalizedId)
    {
        normalizedId = string.Empty;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        var candidate = providerId.Trim().ToLowerInvariant();
        if (candidate.Length > MaximumLength || candidate.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            return false;
        }

        normalizedId = candidate;
        return true;
    }

    public static bool IsCanonical(string? providerId, out string canonicalId) =>
        TryNormalize(providerId, out canonicalId) &&
        string.Equals(providerId, canonicalId, StringComparison.Ordinal);
}
