namespace QproFaceTracking.Hub;

// Windows 10 is an explicit experiment, not an AMD-supported platform. Keep
// this permission separate from GPU eligibility and runtime verification.
internal static class HubRocmOsPolicy
{
    internal const int MinimumWindows10Build = 19045;
    internal const int MinimumWindows11Build = 22000;

    internal static bool IsWindows10OptInEligible(int build) =>
        build is >= MinimumWindows10Build and < MinimumWindows11Build;

    internal static bool CanInstall(int build, bool allowExperimentalWindows10, bool legacy = false) =>
        build >= MinimumWindows11Build ||
        (!legacy && IsWindows10OptInEligible(build) && allowExperimentalWindows10);

    internal static string[] InstallArguments(int build, bool allowExperimentalWindows10) =>
        IsWindows10OptInEligible(build) && allowExperimentalWindows10
            ? ["-AllowExperimentalWindows10"] : [];

    internal static string? BlockedReason(int build, bool allowExperimentalWindows10)
    {
        if (CanInstall(build, allowExperimentalWindows10)) return null;
        return IsWindows10OptInEligible(build)
            ? "Windows 10 ROCm is experimental. Enable Try ROCm on Windows 10 below to attempt setup. GPU checks must still pass."
            : "ROCm setup needs Windows 11, or Windows 10 22H2 (build 19045 or later) with experimental support enabled. Use the PC runtime on older Windows versions.";
    }
}
