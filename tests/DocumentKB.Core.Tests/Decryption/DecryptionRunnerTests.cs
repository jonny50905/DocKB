using DocumentKB.Core.Configuration;
using DocumentKB.Core.Decryption;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocumentKB.Core.Tests.Decryption;

public class DecryptionRunnerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(),
        "kb-decrypt-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }

    private DecryptionRunner Make(DecryptionOptions opts) =>
        new(Options.Create(new KbOptions { Decryption = opts }),
            NullLogger<DecryptionRunner>.Instance);

    private static string FixtureScript() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "scripts", "Identity-Decrypt.ps1"));

    [Fact]
    public async Task Disabled_ReturnsPassthrough()
    {
        var src = Path.GetTempFileName();
        var runner = Make(new DecryptionOptions { Enabled = false });
        await using var tf = await runner.DecryptAsync(src, default);
        tf.Path.Should().Be(src);
        File.Delete(src);
    }

    [Fact]
    public async Task Enabled_CopiesInputToTempViaScript()
    {
        var src = Path.GetTempFileName();
        await File.WriteAllTextAsync(src, "hello-aip");
        var runner = Make(new DecryptionOptions
        {
            Enabled = true, PwshExe = "pwsh", ScriptPath = FixtureScript(),
            TempRoot = _tempRoot, TimeoutSeconds = 30,
        });
        await using (var tf = await runner.DecryptAsync(src, default))
        {
            File.ReadAllText(tf.Path).Should().Be("hello-aip");
            tf.Path.Should().StartWith(_tempRoot);
        }
        Directory.GetDirectories(_tempRoot).Should().BeEmpty(
            "temp subdir must be deleted on disposal");
        File.Delete(src);
    }

    [Fact]
    public async Task NonZeroExit_Throws_AndCleansTemp()
    {
        Directory.CreateDirectory(_tempRoot);
        var failScript = Path.Combine(_tempRoot, "fail.ps1");
        await File.WriteAllTextAsync(failScript, "exit 7");
        var src = Path.GetTempFileName();
        var runner = Make(new DecryptionOptions
        {
            Enabled = true, PwshExe = "pwsh", ScriptPath = failScript,
            TempRoot = _tempRoot, TimeoutSeconds = 30,
        });
        var act = async () => await runner.DecryptAsync(src, default);
        await act.Should().ThrowAsync<DecryptionException>();
        // verify no leftover GUID-named subdir (fail.ps1 itself is fine)
        var leftover = Directory.GetDirectories(_tempRoot);
        leftover.Should().BeEmpty();
        File.Delete(src);
    }
}
