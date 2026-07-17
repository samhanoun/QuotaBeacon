using QuotaBeacon.Core.Providers.Claude;

namespace QuotaBeacon.Core.Tests;

public sealed class ClaudeCredentialReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quotabeacon-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Reader_reads_only_the_claude_oauth_access_token()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"expected","refreshToken":"must-not-be-used"},"other":"ignored"}""",
            CancellationToken.None);
        var reader = new ClaudeCredentialReader(_directory);

        var token = await reader.ReadAccessTokenAsync(CancellationToken.None);

        Assert.Equal("expected", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("{\"claudeAiOauth\":{}}")]
    public async Task Reader_returns_null_for_missing_or_unreadable_credentials(string? contents)
    {
        Directory.CreateDirectory(_directory);
        if (contents is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_directory, ".credentials.json"),
                contents,
                CancellationToken.None);
        }

        var reader = new ClaudeCredentialReader(_directory);

        Assert.Null(await reader.ReadAccessTokenAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
