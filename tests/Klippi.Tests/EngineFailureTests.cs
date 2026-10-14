using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;
public sealed class EngineFailureTests
{
    [Theory]
    [InlineData("ERROR: Private video. Sign in", "private")]
    [InlineData("ERROR: Sign in to confirm your age", "Sign-in")]
    [InlineData("Could not copy Chrome cookies database (locked)", "Browser sign-in")]
    [InlineData("Requested format is not available", "unavailable")]
    [InlineData("Unsupported URL", "not supported")]
    [InlineData("Unable to extract uploader id", "website may have changed")]
    [InlineData("Unable to download webpage: network is unreachable", "internet")]
    [InlineData("No space left on device", "disk space")]
    public void Recognized_failure_is_plain_language_and_never_copies_raw_secrets(string error, string expected)
    {
        var result = new ProcessResult(1, "", error + "\nAuthorization: Bearer sensitive-token\nC:\\Users\\Private\\cookies.sqlite\nhttps://example.com/media?token=secret");
        var exception = Assert.Throws<MediaOperationException>(() => EngineSupport.Success(result, "Download"));
        Assert.Contains(expected, exception.Message); Assert.DoesNotContain("sensitive-token", exception.Message); Assert.DoesNotContain("Private\\", exception.Message); Assert.DoesNotContain("?token", exception.Message); Assert.Null(exception.Details);
    }
}
