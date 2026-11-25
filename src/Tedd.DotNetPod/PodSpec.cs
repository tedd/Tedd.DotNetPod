using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tedd.DotNetPod;

public sealed record DirectoryMapping(string Source, string Target);

/// <summary>Daemon-local paths are supplied by the trusted host operator.</summary>
public sealed record PodSpec(string Id, string Image, string Application, string EntryPoint, string Data,
    int MemoryMiB = 256, int MilliCpu = 1000, int Pids = 128, string? MeshSocket = null,
    string? MeshKey = null, string[]? Arguments = null, string? ReadinessFile = null, string? LivenessFile = null,
    DirectoryMapping[]? DirectoryMounts = null)
{
    public const int UserId = 1654;
    public string Fingerprint
    {
        get
        {
            object identity = new { Id, Image, Application, EntryPoint, Data, MemoryMiB, MilliCpu, Pids, MeshSocket, MeshKey, Arguments, ReadinessFile };
            if (LivenessFile != null) identity = new { Spec = identity, LivenessFile };
            if (DirectoryMounts is { Length: > 0 }) identity = new { Spec = identity, DirectoryMounts = DirectoryMounts.OrderBy(m => m.Target, StringComparer.Ordinal) };
            return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));
        }
    }

    public static void ValidateId(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,47}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Expected a lower-case instance name of 1–48 characters.");
    }

    public void Validate()
    {
        ValidateId(Id);
        if (Image is null || !Regex.IsMatch(Image, @"\Asha256:[0-9a-f]{64}\z"))
            throw new ArgumentException("An installed immutable image ID is required.");
        if (EntryPoint is null || !Regex.IsMatch(EntryPoint, @"\A[A-Za-z0-9_][A-Za-z0-9._-]{0,180}\.dll\z"))
            throw new ArgumentException("EntryPoint must name a DLL in the application directory.");
        if (ReadinessFile != null) ValidateMarker(ReadinessFile);
        if (LivenessFile != null) ValidateMarker(LivenessFile);
        if (MemoryMiB is < 64 or > 1048576 || MilliCpu is < 10 or > 256000 || Pids is < 16 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(MemoryMiB), "Invalid resource limit.");
        if ((MeshSocket == null) != (MeshKey == null)) throw new ArgumentException("Supply both mesh mounts.");
        var mounts = DirectoryMounts ?? [];
        if (mounts.Any(m => m == null || !CanonicalPath(m.Source) || !CanonicalPath(m.Target)))
            throw new ArgumentException("Directory mounts require canonical absolute Linux paths.");
        var paths = new[] { Application, Data, MeshSocket, MeshKey }.OfType<string>().Concat(mounts.Select(m => m.Source)).ToArray();
        if (Application == null || Data == null || paths.Any(x => !CanonicalPath(x)))
            throw new ArgumentException("Mount sources must be canonical absolute Linux paths.");
        if (AnyOverlap(paths))
            throw new ArgumentException("Mount sources overlap.");
        var targets = mounts.Select(m => m.Target).ToArray();
        string[] reserved = ["/data", "/mesh", "/tmp", "/dev", "/proc", "/sys", "/bin", "/sbin", "/lib", "/lib64",
            "/usr", "/etc", "/run", "/var/run", "/app/" + EntryPoint];
        if (AnyOverlap(targets) || targets.Any(p => p == "/app" || reserved.Any(q => Overlap(p, q))))
            throw new ArgumentException("Directory mounts overlap a reserved path or another mount.");
        if (Arguments is { Length: > 128 } || Arguments?.Any(s => s == null || s.Length > 8192 || s.Contains('\0')) == true)
            throw new ArgumentException("Invalid program arguments.");
    }

    static bool CanonicalPath(string path) => path is { Length: > 1 and <= 4096 } && path.StartsWith('/') &&
        path.Split('/').Skip(1).All(p => p is not ("" or "." or "..") && !p.Any(char.IsControl));

    static bool Overlap(string left, string right) => left == right || left.StartsWith(right + "/", StringComparison.Ordinal) ||
        right.StartsWith(left + "/", StringComparison.Ordinal);

    static bool AnyOverlap(string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
            for (int j = i + 1; j < paths.Length; j++)
                if (Overlap(paths[i], paths[j])) return true;
        return false;
    }

    public static void ValidateMarker(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A[A-Za-z0-9_][A-Za-z0-9._-]{0,100}\z"))
            throw new ArgumentException("Invalid health marker.");
    }
}
