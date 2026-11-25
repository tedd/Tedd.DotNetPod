using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Tedd.DotNetPod;

public sealed record PodStatus(string Id, string Container, string Fingerprint, string State, bool Ready, long Restarts,
    DateTimeOffset StartedAt = default, DateTimeOffset? LastHeartbeat = null);
public sealed class PodConflictException(string message) : InvalidOperationException(message);

/// <summary>Controls instances owned by one named scope on a local Linux Docker engine.</summary>
public sealed class PodHost : IDisposable
{
    const string Api = "/v1.44";
    const string Owner = "net.tedd.dotnetpod.scope", Instance = "net.tedd.dotnetpod.instance", Revision = "net.tedd.dotnetpod.spec";
    readonly HttpClient client;
    readonly string scope;
    readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    public PodHost(string scope, HttpMessageHandler handler)
    {
        PodSpec.ValidateId(scope);
        this.scope = scope;
        client = new(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(90) };
    }

    public static PodHost Open(string scope, string socket = "/var/run/docker.sock") => new(scope, new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectCallback = async (_, token) =>
        {
            var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), token);
                return new NetworkStream(connection, ownsSocket: true);
            }
            catch { connection.Dispose(); throw; }
        }
    });

    SemaphoreSlim Gate(string id) => gates[(uint)StringComparer.Ordinal.GetHashCode(id) % gates.Length];
    string Name(string id) => $"dotnetpod.{scope}.{id}";

    public async Task<string[]> InstalledImagesAsync(CancellationToken ct = default)
    {
        using var response = await client.GetAsync($"{Api}/images/json", ct);
        await Check(response, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return doc.RootElement.EnumerateArray().Select(row => row.GetProperty("Id").GetString()!).ToArray();
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        var filter = JsonSerializer.Serialize(new { label = new[] { Owner + "=" + scope } });
        using var response = await client.GetAsync($"{Api}/containers/json?all=1&filters={Uri.EscapeDataString(filter)}", ct);
        await Check(response, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var ids = new List<string>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var labels = row.GetProperty("Labels");
            if (!labels.TryGetProperty(Instance, out var name) || name.GetString() is not string id) continue;
            PodSpec.ValidateId(id);
            if (row.GetProperty("Names").EnumerateArray().Any(n => n.GetString() == "/" + Name(id))) ids.Add(id);
        }
        return ids;
    }

    public async Task<PodStatus?> InspectAsync(string id, CancellationToken ct = default)
    {
        PodSpec.ValidateId(id);
        using var result = await client.GetAsync($"{Api}/containers/{Name(id)}/json", ct);
        if (result.StatusCode == HttpStatusCode.NotFound) return null;
        await Check(result, ct);
        using var doc = JsonDocument.Parse(await result.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        var labels = root.GetProperty("Config").GetProperty("Labels");
        if (labels.ValueKind != JsonValueKind.Object || !labels.TryGetProperty(Owner, out var owner) || owner.GetString() != scope ||
            !labels.TryGetProperty(Instance, out var instance) || instance.GetString() != id ||
            !labels.TryGetProperty(Revision, out var revision))
            throw new PodConflictException($"Instance name {id} is owned by another application.");
        var state = root.GetProperty("State");
        var status = state.GetProperty("Status").GetString()!;
        var hasHealth = state.TryGetProperty("Health", out var health);
        var ready = status == "running" && (!hasHealth ||
            health.GetProperty("Status").GetString() == "healthy");
        DateTimeOffset? heartbeat = null;
        if (hasHealth && health.TryGetProperty("Log", out var log) && log.ValueKind == JsonValueKind.Array && log.GetArrayLength() > 0 &&
            long.TryParse(log[log.GetArrayLength() - 1].GetProperty("Output").GetString()?.Trim(), out var seconds) &&
            seconds is >= 0 and <= 253402300799)
            heartbeat = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return new(id, root.GetProperty("Id").GetString()!, revision.GetString()!, status, ready,
            root.GetProperty("RestartCount").GetInt64(), state.TryGetProperty("StartedAt", out var start) && start.TryGetDateTimeOffset(out var at) ? at : default, heartbeat);
    }

    public async Task<PodStatus> EnsureAsync(PodSpec spec, CancellationToken ct = default)
    {
        spec = spec with { Arguments = spec.Arguments?.ToArray(), DirectoryMounts = spec.DirectoryMounts?.ToArray() };
        spec.Validate();
        var gate = Gate(spec.Id);
        await gate.WaitAsync(ct);
        try
        {
            var pod = await InspectAsync(spec.Id, ct);
            if (pod == null)
            {
                using var result = await client.PostAsJsonAsync($"{Api}/containers/create?name={Name(spec.Id)}", Configuration(spec), JsonSerializerOptions.Default, ct);
                if (result.StatusCode != HttpStatusCode.Conflict) await Check(result, ct);
                pod = await InspectAsync(spec.Id, ct) ?? throw new IOException("Container disappeared during creation.");
            }
            if (pod.Fingerprint != spec.Fingerprint) throw new PodConflictException($"Instance {spec.Id} has a different specification.");
            if (pod.State is "created" or "exited")
            {
                using var result = await client.PostAsync($"{Api}/containers/{pod.Container}/start", null, ct);
                if (result.StatusCode != HttpStatusCode.NotModified) await Check(result, ct);
                return await InspectAsync(spec.Id, ct) ?? throw new IOException("Container disappeared during startup.");
            }
            return pod;
        }
        finally { gate.Release(); }
    }

    public Task StopAsync(string id, int graceSeconds = 10, CancellationToken ct = default) => StopOrRemove(id, graceSeconds, false, ct);
    public Task UnloadAsync(string id, int graceSeconds = 10, CancellationToken ct = default) => StopOrRemove(id, graceSeconds, true, ct);

    async Task StopOrRemove(string id, int grace, bool remove, CancellationToken ct)
    {
        PodSpec.ValidateId(id);
        if (grace is < 0 or > 60) throw new ArgumentOutOfRangeException(nameof(grace));
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var pod = await InspectAsync(id, ct);
            if (pod == null) return;
            if (pod.State is not ("created" or "exited" or "dead"))
            {
                using var result = await client.PostAsync($"{Api}/containers/{pod.Container}/stop?t={grace}", null, ct);
                if (result.StatusCode is not (HttpStatusCode.NotModified or HttpStatusCode.NotFound)) await Check(result, ct);
            }
            if (remove)
            {
                using var result = await client.DeleteAsync($"{Api}/containers/{pod.Container}?v=1", ct);
                if (result.StatusCode != HttpStatusCode.NotFound) await Check(result, ct);
            }
        }
        finally { gate.Release(); }
    }

    object Configuration(PodSpec spec)
    {
        var mounts = new List<object>();
        void Mount(string path, string target, bool readOnly) => mounts.Add(new
        {
            Type = "bind",
            Source = path,
            Target = target,
            ReadOnly = readOnly,
            BindOptions = new { Propagation = "rprivate", NonRecursive = true }
        });
        Mount(spec.Application, "/app", true);
        Mount(spec.Data, "/data", false);
        foreach (var mapping in spec.DirectoryMounts ?? []) Mount(mapping.Source, mapping.Target, false);
        if (spec.MeshSocket != null)
        {
            Mount(spec.MeshSocket, "/mesh/agent.sock", true);
            Mount(spec.MeshKey!, "/mesh/key", true);
        }
        return new
        {
            spec.Image,
            User = $"{PodSpec.UserId}:{PodSpec.UserId}",
            WorkingDir = "/app",
            Entrypoint = new[] { "dotnet" },
            Cmd = new[] { "/app/" + spec.EntryPoint }.Concat(spec.Arguments ?? []),
            Env = new[] { "HOME=/tmp", "DOTNET_EnableDiagnostics=0", "DOTNET_CLI_TELEMETRY_OPTOUT=1" },
            Labels = new Dictionary<string, string> { [Owner] = scope, [Instance] = spec.Id, [Revision] = spec.Fingerprint },
            Healthcheck = Healthcheck(spec),
            HostConfig = new
            {
                Mounts = mounts,
                NetworkMode = "none",
                IpcMode = "private",
                CgroupnsMode = "private",
                ReadonlyRootfs = true,
                CapDrop = new[] { "ALL" },
                SecurityOpt = new[] { "no-new-privileges=true" },
                Memory = (long)spec.MemoryMiB << 20,
                MemorySwap = (long)spec.MemoryMiB << 20,
                NanoCpus = (long)spec.MilliCpu * 1000000,
                PidsLimit = spec.Pids,
                Tmpfs = new Dictionary<string, string> { ["/tmp"] = "rw,nosuid,nodev,noexec,size=32m,mode=1777" },
                LogConfig = new { Type = "json-file", Config = new Dictionary<string, string> { ["max-size"] = "4m", ["max-file"] = "2" } },
                RestartPolicy = new { Name = "unless-stopped" }
            }
        };
    }

    static object Healthcheck(PodSpec spec)
    {
        if (spec.LivenessFile == null && spec.ReadinessFile == null) return new { Test = new[] { "NONE" } };
        string[] command = spec.LivenessFile == null ? ["CMD", "/usr/bin/test", "-f", "/tmp/" + spec.ReadinessFile] :
            ["CMD", "/bin/sh", "-c", """
                /usr/bin/stat -c %Y -- "/tmp/$1" 2>/dev/null
                [ -z "$2" ] || /usr/bin/test -f "/tmp/$2"
                """, "health", spec.LivenessFile, spec.ReadinessFile ?? ""];
        return new { Test = command, Interval = 2000000000L, Timeout = 1000000000L, Retries = 2 };
    }

    static async Task Check(HttpResponseMessage result, CancellationToken ct)
    {
        if (!result.IsSuccessStatusCode)
            throw new HttpRequestException($"Container engine returned {(int)result.StatusCode}: {await result.Content.ReadAsStringAsync(ct)}", null, result.StatusCode);
    }
    public void Dispose() => client.Dispose();
}
