# Tedd.DotNetPod

Tedd.DotNetPod is a .NET 11 CLI and library for running framework-dependent .NET applications in named Linux containers. An operator supplies the published application, a local runtime image and a data directory. The host uses the Docker Engine API to start the application, inspect its health and remove its container while retaining its data.

Each instance runs as UID/GID 1654 with a read-only application and root filesystem, loopback-only networking, and limits on memory, CPU and processes. The Docker socket stays on the host. Access to it grants administrative control over the engine, so only a trusted operator should supply image IDs and mount paths.

## Run the sample

Use a Linux host with the .NET 11 SDK, a local Docker Engine supporting API v1.44, and access to its Unix socket. Run these commands in Bash on the engine host. The bundled fixture writes its runtime observations to `/data/report`, creates `/tmp/ready` and waits.

```bash
git clone https://github.com/tedd/Tedd.DotNetPod.git
cd Tedd.DotNetPod
sample=$(mktemp -d /tmp/dotnetpod.XXXXXXXX)
dotnet publish tests/Fixture -c Release --no-self-contained -o "$sample/app"
sudo install -d -o 1654 -g 1654 -m 0755 "$sample/data"

docker pull mcr.microsoft.com/dotnet/runtime:11.0
image=$(docker image inspect --format '{{.Id}}' mcr.microsoft.com/dotnet/runtime:11.0)
cat > "$sample/pod.json" <<EOF
{
  "Id": "worker",
  "Image": "$image",
  "Application": "$sample/app",
  "EntryPoint": "Fixture.dll",
  "Data": "$sample/data",
  "Arguments": ["report"],
  "ReadinessFile": "ready"
}
EOF
```

DotNetPod starts the installed image by its immutable ID. For another application, publish its project into a readable directory and set `EntryPoint` to its DLL filename. Choose a runtime image that supplies the application's required shared frameworks. The default socket is `/var/run/docker.sock`. For another local engine, set `DOTNETPOD_SOCKET` and point the Docker CLI at the same socket.

```bash
pod() { dotnet run --project src/Tedd.DotNetPod -- "$@"; }
pod run demo "$sample/pod.json"
pod status demo worker
```

Repeat `status` until the JSON shows `"Ready":true`. Then run `cat "$sample/data/report"` to inspect the .NET major version, container hostname, network interfaces and enforced limits.

## Manage an instance

| Command | Effect |
| --- | --- |
| `pod run demo "$sample/pod.json"` | Creates the instance, starts it if stopped, or returns the matching running instance. |
| `pod status demo worker` | Returns JSON with the engine state, readiness and restart count. An absent instance returns `null`. |
| `pod stop demo worker` | Stops the application and retains its container for a subsequent run. |
| `pod unload demo worker` | Stops and removes the container. The host data directory persists. |

The scope (`demo`) and instance ID (`worker`) identify a container together. Reusing that pair with a changed specification produces a conflict. To replace it, unload the old instance and run the new specification. The default limits are 256 MiB, one CPU and 128 processes. Override them with `MemoryMiB`, `MilliCpu` and `Pids`.

## Use from a supervisor

Reference the `src/Tedd.DotNetPod` project and open a `PodHost` for the operator scope. A new host reads existing instances from Docker metadata.

```csharp
using Tedd.DotNetPod;

using var host = PodHost.Open("demo");
foreach (var id in await host.ListAsync())
{
    var status = await host.InspectAsync(id);
    if (status is not null)
        Console.WriteLine($"{id}: {status.State}, ready={status.Ready}");
}
```

See the [runtime specification](SPEC.md) for mount requirements, optional HostMesh credentials, health markers and live Docker test setup. Run the ordinary test suite with `dotnet test Tedd.DotNetPod.slnx`.
