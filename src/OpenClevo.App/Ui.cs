using OpenClevo.Core;

namespace OpenClevo.App;

static class Ui
{
    public static readonly (PowerMode Mode, string Name)[] Modes =
    [
        (PowerMode.Quiet, "Sessiz"),
        (PowerMode.PowerSaving, "Güç tasarrufu"),
        (PowerMode.Entertainment, "Eğlence"),
        (PowerMode.Performance, "Performans"),
    ];

    public static string ModeName(PowerMode m) => Modes.FirstOrDefault(x => x.Mode == m).Name ?? m.ToString();

    public static string FanName(int index) => index switch { 1 => "CPU", 2 => "GPU", _ => $"Fan {index}" };
}
