using System.Text.Json;
using Tedd.DotNetPod;

if (args.Length != 3 || args[0] is not ("run" or "status" or "stop" or "unload"))
{
    Console.Error.WriteLine("dotnetpod <run|status|stop|unload> <scope> <spec.json|instance-id>");
    return 2;
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
    using var host = PodHost.Open(args[1], Environment.GetEnvironmentVariable("DOTNETPOD_SOCKET") ?? "/var/run/docker.sock");
    switch (args[0])
    {
        case "run":
            var spec = JsonSerializer.Deserialize<PodSpec>(await File.ReadAllTextAsync(args[2], shutdown.Token)) ?? throw new InvalidDataException("Empty specification.");
            Console.WriteLine(JsonSerializer.Serialize(await host.EnsureAsync(spec, shutdown.Token)));
            break;
        case "status": Console.WriteLine(JsonSerializer.Serialize(await host.InspectAsync(args[2], shutdown.Token))); break;
        case "stop": await host.StopAsync(args[2], ct: shutdown.Token); break;
        case "unload": await host.UnloadAsync(args[2], ct: shutdown.Token); break;
    }
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
