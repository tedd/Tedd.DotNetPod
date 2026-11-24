using System.Net.NetworkInformation;

var suffix = args.Single();
File.WriteAllText("/data/" + suffix, string.Join('\n', new[] {
    Environment.Version.Major.ToString(), Environment.MachineName,
    string.Join(',', NetworkInterface.GetAllNetworkInterfaces().Select(i => i.Name)),
    File.ReadLines("/proc/self/status").Single(s => s.StartsWith("CapEff:")),
    File.ReadLines("/proc/self/status").Single(s => s.StartsWith("NoNewPrivs:")),
    File.ReadAllText("/sys/fs/cgroup/memory.max").Trim(),
    File.ReadAllText("/sys/fs/cgroup/pids.max").Trim()
}));
File.WriteAllText("/tmp/ready", "");
await Task.Delay(Timeout.InfiniteTimeSpan);
