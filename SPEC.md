# Tedd.DotNetPod runtime specification

DotNetPod runs framework-dependent .NET 11 programs in independently named Linux containers. A trusted local operator provisions the runtime image, application tree, writable data directory and optional HostMesh socket and credential. The managed host talks to a local Linux Docker Engine using its Unix socket and API v1.44. Access to the engine socket is an administrative privilege. [Docker Engine API](https://docs.docker.com/reference/api/engine/version/v1.44/)

## Isolation contract

Each instance gets an independent PID namespace, hostname, private IPC namespace, loopback-only network namespace, capped CPU, memory, process count, and a bounded ephemeral /tmp. The root filesystem and published application are read-only. The runtime runs as UID and GID 1654, drops all Linux capabilities and sets no-new-privileges. The host kernel and container engine belong to the trust boundary. Operators must trust both the image and the host-supplied mount paths, protect their parent directories, and verify the engine enforces its resource limits. AssemblyLoadContext provides assembly loading and unloading inside one process. Operating-system isolation is required for executing mutually untrusted code. [Microsoft plugin guidance](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)

The writable /data mount persists across stops and unloads. Supply a separate source per instance for private data or an explicitly shared volume for concurrent access. Mesh-enabled applications receive a read-only Unix socket at /mesh/agent.sock and one application key file at /mesh/key. The engine socket is reserved for the host. A network policy belongs to the HostMesh identity associated with that key.

## Lifecycle

The tuple of operator scope and instance ID names one engine object. A SHA-256 fingerprint covers the full launch specification. Concurrent ensure operations converge on the engine's unique name, verify ownership and fingerprint, and start a created or stopped instance. An incompatible existing instance returns a conflict. Stop delivers the image stop signal with a configurable deadline followed by forced termination. Unload stops and removes the specific container ID, preserving bind-mounted files. A fresh host object reconstructs status from engine metadata. An interrupted operation can be retried with the same specification.

Running and Ready are distinct states. An optional marker under /tmp enables an engine health probe that calls /usr/bin/test. A program must remove the marker if it loses readiness. Without a probe, Running counts as Ready. The engine's unless-stopped policy restarts exited programs and resumes eligible instances after engine restart. Replacements require explicit unload. Processes with health failures remain inspectable.

LivenessFile optionally names a second marker under /tmp. The engine samples its modification time inside the running container and exposes it as LastHeartbeat. Images using this option must supply /bin/sh and /usr/bin/stat. Supervisors can distinguish a stalled program from one that is temporarily unable to receive new traffic.

## Invocation

Publish the application for .NET 11 on Linux. Install a compatible runtime image on the local engine and obtain its immutable sha256 image ID using docker image inspect. Create the writable directory with UID 1654 access. Run the following commands with a .NET 11 SDK. The CLI uses /var/run/docker.sock by default. DOTNETPOD_SOCKET selects another local engine.

```json
{
  "Id": "web-a",
  "Image": "sha256:<64 lowercase hexadecimal digits>",
  "Application": "/srv/releases/web-42",
  "EntryPoint": "Web.dll",
  "Data": "/srv/data/web-a",
  "MemoryMiB": 256,
  "MilliCpu": 500,
  "Pids": 128
}
```

```sh
dotnet run --project src/Tedd.DotNetPod -- run team web-a.json
dotnet run --project src/Tedd.DotNetPod -- status team web-a
dotnet run --project src/Tedd.DotNetPod -- stop team web-a
dotnet run --project src/Tedd.DotNetPod -- unload team web-a
```

## Verification

Run dotnet test. The live Docker test additionally needs POD_TEST_SOCKET, POD_TEST_IMAGE and POD_TEST_DIRECTORY pointing to a disposable Linux engine, an installed .NET 11 runtime image ID, and a test directory. Publish tests/Fixture in the app subdirectory and grant UID 1654 write access to two sibling directories named a and b. The test verifies runtime version, namespace separation, kernel resource limits, independent files, readiness, shutdown and adoption after host reconstruction.
