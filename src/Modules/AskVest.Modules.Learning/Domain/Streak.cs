namespace AskVest.Modules.Learning.Domain;

internal sealed record StreakResult(int Current, int Longest);

/// <summary>
/// Pure streak math over counted UTC learning days. The current streak stays
/// alive through today and yesterday; a gap of a full day resets it to zero.
/// </summary>
internal static class Streak
{
    public static StreakResult Compute(IReadOnlyCollection<DateOnly> days, DateOnly today)
    {
        if (days.Count == 0)
        {
            return new StreakResult(0, 0);
        }

        var sorted = days.Distinct().Order().ToList();
        var longest = LongestRun(sorted);

        var anchor = sorted.Contains(today) ? today
            : sorted.Contains(today.AddDays(-1)) ? today.AddDays(-1)
            : (DateOnly?)null;
        if (anchor is null)
        {
            return new StreakResult(0, longest);
        }

        var current = 0;
        var cursor = anchor.Value;
        while (sorted.Contains(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        return new StreakResult(current, longest);
    }

    private static int LongestRun(List<DateOnly> sorted)
    {
        var best = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var day in sorted)
        {
            run = previous is { } last && day == last.AddDays(1) ? run + 1 : 1;
            best = Math.Max(best, run);
            previous = day;
        }

        return best;
    }
}
