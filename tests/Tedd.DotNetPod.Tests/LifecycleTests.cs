using System.Net;
using System.Text;
using System.Text.Json;
using Tedd.DotNetPod;

namespace Tedd.DotNetPod.Tests;

public class LifecycleTests
{
    internal static PodSpec Spec => new("sample", "sha256:" + new string('1',64), "/srv/app", "Sample.dll", "/srv/data");

    [Theory]
    [InlineData("")][InlineData("x/y")][InlineData("A")][InlineData("-x")][InlineData("x\n")][InlineData("a.b")]
    public void InvalidIdentity(string id) => Assert.Throws<ArgumentException>(() => (Spec with { Id = id }).Validate());

    [Theory]
    [InlineData("/")][InlineData("relative")][InlineData("/a/../b")][InlineData("/a/./b")][InlineData("/a//b")]
    [InlineData("/srv/app")][InlineData("/srv/app/child")][InlineData("/srv")][InlineData("/x/")]
    public void RejectsAmbiguousOrOverlappingMounts(string path) =>
        Assert.Throws<ArgumentException>(() => (Spec with { Data = path }).Validate());

    [Theory]
    [InlineData("../app.dll")][InlineData("/etc/x.dll")][InlineData("a.exe")][InlineData("a\nb.dll")]
    public void InvalidEntrypoint(string value) => Assert.Throws<ArgumentException>(() => (Spec with { EntryPoint = value }).Validate());

    [Fact]
    public void ResourcesAndImageIdentityAreChecked()
    {
        foreach (var spec in new[] { Spec with { Pids = 15 }, Spec with { Pids = 32769 }, Spec with { MilliCpu = 9 },
            Spec with { MemoryMiB = 63 }, Spec with { MemoryMiB = 1048577 }, Spec with { MilliCpu = 256001 },
            Spec with { Image = "runtime:latest" }, Spec with { MeshKey = "/key" }, Spec with { MeshSocket = "/socket" },
            Spec with { ReadinessFile = "../ready" }, Spec with { Arguments = ["a\0b"] } })
            Assert.ThrowsAny<ArgumentException>(spec.Validate);
    }

    [Fact]
    public async Task ConcurrentEnsureCreatesOnceAndControllerRestartAdopts()
    {
        using var engine = new Engine();
        using var host = new PodHost("test", engine);
        var states = await Task.WhenAll(Enumerable.Range(0,64).Select(_ => Task.Run(() => host.EnsureAsync(Spec))));
        Assert.All(states, s => Assert.True(s.Ready));
        Assert.Equal(1, engine.Created);
        using var recovered = new PodHost("test", engine);
        Assert.True((await recovered.EnsureAsync(Spec)).Ready);
        Assert.Equal(1, engine.Created);
        await recovered.StopAsync("sample");
        Assert.Equal("exited", (await host.InspectAsync("sample"))!.State);
        Assert.True((await host.EnsureAsync(Spec)).Ready);
        await Task.WhenAll(Enumerable.Range(0,32).Select(_ => host.UnloadAsync("sample")));
        Assert.Null(await host.InspectAsync("sample"));
    }

    [Fact]
    public async Task DifferentRevisionRequiresExplicitReplacement()
    {
        using var host = new PodHost("test", new Engine());
        var original = await host.EnsureAsync(Spec);
        await Assert.ThrowsAsync<PodConflictException>(() => host.EnsureAsync(Spec with { MemoryMiB = 512 }));
        Assert.Equal(original, await host.InspectAsync("sample"));
    }

    [Fact]
    public async Task ForeignNameCannotBeStoppedOrAdopted()
    {
        using var engine = new Engine();
        using var host = new PodHost("test", engine);
        await host.EnsureAsync(Spec);
        engine.Foreign = true;
        await Assert.ThrowsAsync<PodConflictException>(() => host.UnloadAsync("sample"));
        await Assert.ThrowsAsync<PodConflictException>(() => host.EnsureAsync(Spec));
        Assert.Equal("running", engine.State);
    }

    [Fact]
    public async Task LostCreateResponseIsAdoptedOnRetry()
    {
        using var engine = new Engine { LoseCreateResponse = true };
        using var host = new PodHost("test", engine);
        await Assert.ThrowsAsync<IOException>(() => host.EnsureAsync(Spec));
        Assert.True((await host.EnsureAsync(Spec)).Ready);
        Assert.Equal(1,engine.Created);
    }

    [Fact]
    public async Task EngineReceivesIsolationAndResourcePolicy()
    {
        using var engine = new Engine();
        using var host = new PodHost("test", engine);
        await host.EnsureAsync(Spec with { MeshKey = "/keys/app", MeshSocket = "/ipc/socket", ReadinessFile = "ready" });
        var root = engine.Config;
        var cfg = root.GetProperty("HostConfig");
        Assert.Equal("none", cfg.GetProperty("NetworkMode").GetString());
        Assert.Equal("ALL", cfg.GetProperty("CapDrop")[0].GetString());
        Assert.True(cfg.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.Equal(256L << 20,cfg.GetProperty("Memory").GetInt64());
        Assert.Equal(1000000000L,cfg.GetProperty("NanoCpus").GetInt64());
        Assert.Equal(128,cfg.GetProperty("PidsLimit").GetInt32());
        Assert.Equal("1654:1654",root.GetProperty("User").GetString());
        Assert.Equal(new[]{true,false,true,true},cfg.GetProperty("Mounts").EnumerateArray().Select(m=>m.GetProperty("ReadOnly").GetBoolean()));
        Assert.Equal("CMD",root.GetProperty("Healthcheck").GetProperty("Test")[0].GetString());
    }

    sealed class Engine : HttpMessageHandler
    {
        public JsonElement Config;
        public int Created;
        public bool LoseCreateResponse, Foreign;
        public string? State;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/create"))
            {
                Config = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                Created++;
                State = "created";
                if (LoseCreateResponse) { LoseCreateResponse = false; throw new IOException("Disconnected"); }
            }
            else if (State == null) return new(HttpStatusCode.NotFound);
            else if (path.EndsWith("/json"))
            {
                var body = JsonSerializer.Serialize(new { Id = "123", RestartCount = 0,
                    Config = new { Labels = Foreign ? (object)new {} : Config.GetProperty("Labels") }, State = new { Status = State } });
                return new(HttpStatusCode.OK) { Content = new StringContent(body,Encoding.UTF8,"application/json") };
            }
            else if (path.EndsWith("/start")) State = "running";
            else if (path.EndsWith("/stop")) State = "exited";
            else if (request.Method == HttpMethod.Delete) State = null;
            else throw new InvalidOperationException(path);
            return new(HttpStatusCode.OK);
        }
    }
}
