using OpenClevo.Localization;
using OpenClevo.Core;

namespace OpenClevo.App;

static class Ui
{
    public static readonly (PowerMode Mode, string Name)[] Modes =
    [
        (PowerMode.Quiet, Loc.T("power.quiet")),
        (PowerMode.PowerSaving, Loc.T("power.saving")),
        (PowerMode.Entertainment, Loc.T("power.entertainment")),
        (PowerMode.Performance, Loc.T("power.performance")),
    ];

    public static string ModeName(PowerMode m) => Modes.FirstOrDefault(x => x.Mode == m).Name ?? m.ToString();

    public static string FanName(int index) => index switch { 1 => "CPU", 2 => "GPU", _ => $"Fan {index}" };
}
