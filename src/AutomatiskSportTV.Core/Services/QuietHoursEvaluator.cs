using System.Globalization;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Evaluates whether the current local time falls inside configured quiet hours (e.g. 23:30–07:00).
/// </summary>
public static class QuietHoursEvaluator
{
    public static bool IsQuietNow(RankingProfile profile, TimeOnly currentLocalTime)
    {
        if (!profile.EnableQuietHours)
        {
            return false;
        }

        if (!TryParseTime(profile.QuietHoursStart, out var start) ||
            !TryParseTime(profile.QuietHoursEnd, out var end))
        {
            return false;
        }

        if (start == end)
        {
            return false;
        }

        // Handles overnight windows such as 23:30 -> 07:00 cleanly
        if (start > end)
        {
            return currentLocalTime >= start || currentLocalTime < end;
        }

        return currentLocalTime >= start && currentLocalTime < end;
    }

    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        return TimeOnly.TryParseExact(
            (text ?? string.Empty).Trim(),
            ["HH:mm", "H:mm"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);
    }
}
