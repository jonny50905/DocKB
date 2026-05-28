using System.CommandLine;
using DocumentKB.Core.Decryption;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentKB.Ingestion.Commands;

public static class TestDecryptCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var file = new Option<string>("--file", description: "absolute path to test")
            { IsRequired = true };
        var cmd = new Command("test-decrypt", "Run decrypt script for one file") { file };
        cmd.SetHandler(async (string f) =>
        {
            var runner = sp.GetRequiredService<DecryptionRunner>();
            await using var tf = await runner.DecryptAsync(f, default);
            Console.WriteLine($"OK: plain temp at {tf.Path}");
            Console.WriteLine("(temp will be cleaned when this process exits)");
        }, file);
        return cmd;
    }
}
