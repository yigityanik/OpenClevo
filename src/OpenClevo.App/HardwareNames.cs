using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenClevo.App;

/// <summary>Marketing names of this machine's CPU and GPU, read from the registry (no WMI, no extra permissions).</summary>
static class HardwareNames
{
    public static string Cpu()
    {
        try
        {
            var raw = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string;
            if (string.IsNullOrWhiteSpace(raw)) return "CPU";
            var s = Regex.Replace(raw, @"\((R|TM)\)", "");
            s = Regex.Replace(s, @"\s+CPU\b.*$|\s+\d+-Core Processor.*$|\s+with Radeon.*$|\s+@.*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }
        catch { return "CPU"; }
    }

    /// <summary>The dedicated GPU if there is one (NVIDIA / AMD Radeon), otherwise the first real adapter.</summary>
    public static string Gpu()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return "GPU";
            var names = new List<string>();
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                if (cls.OpenSubKey(sub)?.GetValue("DriverDesc") is string d && !d.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) names.Add(d);
            var pick = names.FirstOrDefault(n => Regex.IsMatch(n, "NVIDIA|GeForce|Radeon RX|Radeon Pro", RegexOptions.IgnoreCase)) ?? names.FirstOrDefault();
            return pick == null ? "GPU" : Regex.Replace(pick, @"\((R|TM)\)", "").Trim();
        }
        catch { return "GPU"; }
    }
}
