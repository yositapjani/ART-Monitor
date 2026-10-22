using System.Reflection;

namespace HwFanControl.App.Services;

public static class AppVersion
{
    public static string Display
    {
        get
        {
            var info = typeof(AppVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip any +git metadata.
                int plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }

            var v = typeof(AppVersion).Assembly.GetName().Version;
            if (v is null)
                return "1.0";
            if (v.Minor == 0 && v.Build == 0 && v.Revision == 0)
                return "1.0";
            return $"1.{v.Minor:D2}";
        }
    }

    public static string ProductName => "ART-Monitor";
}
