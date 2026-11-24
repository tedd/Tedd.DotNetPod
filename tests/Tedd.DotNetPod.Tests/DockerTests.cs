using Tedd.DotNetPod;

namespace Tedd.DotNetPod.Tests;

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("POD_TEST_SOCKET") == null) Skip = "Requires a disposable Linux engine and a published fixture.";
    }
}

public class DockerTests
{
    [DockerFact]
    public async Task TwoIsolatedProgramsSurviveHostRestartAndCanBeUnloadedIndependently()
    {
        var socket = Environment.GetEnvironmentVariable("POD_TEST_SOCKET")!;
        var directory = Environment.GetEnvironmentVariable("POD_TEST_DIRECTORY")!;
        var scope = "test-" + Guid.NewGuid().ToString("N");
        var spec = new PodSpec("first", Environment.GetEnvironmentVariable("POD_TEST_IMAGE")!, directory + "/app",
            "Fixture.dll", directory + "/a", Arguments: ["first"], ReadinessFile: "ready", LivenessFile: "ready");
        var other = spec with { Id = "second", Data = directory + "/b", Arguments = ["second"] };
        using var owner = PodHost.Open(scope, socket);
        try
        {
            Assert.Contains(spec.Image, await owner.InstalledImagesAsync());
            using (var first = PodHost.Open(scope, socket))
                await Task.WhenAll(first.EnsureAsync(spec), first.EnsureAsync(other));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            foreach (var pod in new[] { spec, other })
            {
                while (!(await owner.InspectAsync(pod.Id, deadline.Token))!.Ready) await Task.Delay(100, deadline.Token);
                var report = await File.ReadAllLinesAsync(pod.Data + "/" + pod.Id, deadline.Token);
                Assert.Equal("11", report[0]);
                Assert.Equal("lo", report[2]);
                Assert.EndsWith("0000000000000000", report[3]);
                Assert.EndsWith("1", report[4]);
                Assert.Equal((256L << 20).ToString(), report[5]);
                Assert.Equal("128", report[6]);
            }
            Assert.Equal(new[] { "first", "second" }, (await owner.ListAsync()).Order());
            Assert.NotNull((await owner.InspectAsync("first"))!.LastHeartbeat);
            Assert.False(File.Exists(spec.Data + "/second"));
            Assert.False(File.Exists(other.Data + "/first"));
            Assert.NotEqual(File.ReadAllLines(spec.Data + "/first")[1], File.ReadAllLines(other.Data + "/second")[1]);
            await owner.UnloadAsync("first", 0);
            Assert.Null(await owner.InspectAsync("first"));
            Assert.True((await owner.InspectAsync("second"))!.Ready);
            await owner.StopAsync("second", 0);
            Assert.Equal("exited", (await owner.InspectAsync("second"))!.State);
            Assert.Equal("running", (await owner.EnsureAsync(other)).State);
        }
        finally { await owner.UnloadAsync("first", 0); await owner.UnloadAsync("second", 0); }
    }
}
