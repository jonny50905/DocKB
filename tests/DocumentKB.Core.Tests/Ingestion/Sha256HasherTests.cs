using DocumentKB.Core.Ingestion;
using FluentAssertions;
using Xunit;

namespace DocumentKB.Core.Tests.Ingestion;

public class Sha256HasherTests
{
    [Fact]
    public async Task EmptyFile_ReturnsKnownEmptyHash()
    {
        var path = Path.GetTempFileName();
        try
        {
            var hash = await Sha256Hasher.HashAsync(path);
            hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SameContent_SameHash()
    {
        var a = Path.GetTempFileName();
        var b = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(a, "hello world");
            await File.WriteAllTextAsync(b, "hello world");
            var ha = await Sha256Hasher.HashAsync(a);
            var hb = await Sha256Hasher.HashAsync(b);
            ha.Should().Be(hb);
            ha.Should().HaveLength(64);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public async Task DifferentContent_DifferentHash()
    {
        var a = Path.GetTempFileName();
        var b = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(a, "hello");
            await File.WriteAllTextAsync(b, "world");
            var ha = await Sha256Hasher.HashAsync(a);
            var hb = await Sha256Hasher.HashAsync(b);
            ha.Should().NotBe(hb);
        }
        finally { File.Delete(a); File.Delete(b); }
    }
}
