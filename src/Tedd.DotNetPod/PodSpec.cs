using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tedd.DotNetPod;

/// <summary>Daemon-local paths are supplied by the trusted host operator.</summary>
public sealed record PodSpec(string Id, string Image, string Application, string EntryPoint, string Data,
    int MemoryMiB = 256, int MilliCpu = 1000, int Pids = 128, string? MeshSocket = null,
    string? MeshKey = null, string[]? Arguments = null, string? ReadinessFile = null)
{
    public const int UserId = 1654;
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { Id, Image, Application, EntryPoint, Data, MemoryMiB, MilliCpu, Pids, MeshSocket, MeshKey, Arguments, ReadinessFile })));

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
        if (ReadinessFile != null && !Regex.IsMatch(ReadinessFile, @"\A[A-Za-z0-9_][A-Za-z0-9._-]{0,100}\z"))
            throw new ArgumentException("Invalid readiness marker.");
        if (MemoryMiB is < 64 or > 1048576 || MilliCpu is < 10 or > 256000 || Pids is < 16 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(MemoryMiB), "Invalid resource limit.");
        if ((MeshSocket == null) != (MeshKey == null)) throw new ArgumentException("Supply both mesh mounts.");
        var paths = new[] { Application, Data, MeshSocket, MeshKey }.Where(x => x != null).Cast<string>().ToArray();
        if (Application == null || Data == null || paths.Any(x => !x.StartsWith('/') || x.Length > 4096 ||
            x.Split('/').Skip(1).Any(p => p is "" or "." or ".." || p.Any(char.IsControl))))
            throw new ArgumentException("Mount sources must be canonical absolute Linux paths.");
        if (paths.Any(p => paths.Any(q => q.StartsWith(p + "/", StringComparison.Ordinal))) || paths.Distinct().Count() != paths.Length)
            throw new ArgumentException("Mount sources overlap.");
        if (Arguments is { Length: > 128 } || Arguments?.Any(s => s == null || s.Length > 8192 || s.Contains('\0')) == true)
            throw new ArgumentException("Invalid program arguments.");
    }
}
