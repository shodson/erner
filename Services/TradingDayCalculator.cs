namespace Erner.Services;

public static class TradingDayCalculator
{
    public static DateTime GetNextTradingDay(DateTime fromDate)
    {
        var candidate = fromDate.Date.AddDays(1);
        while (!IsTradingDay(candidate))
        {
            candidate = candidate.AddDays(1);
        }
        return candidate;
    }

    public static bool IsTradingDay(DateTime date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        if (IsMarketHoliday(date))
            return false;
        return true;
    }

    public static bool IsMarketHoliday(DateTime date)
    {
        var holidays = GetHolidaysForYear(date.Year);
        return holidays.Contains(date.Date);
    }

    private static HashSet<DateTime> GetHolidaysForYear(int year)
    {
        var holidays = new HashSet<DateTime>();

        // New Year's Day - Jan 1 (observed)
        holidays.Add(ObservedDate(new DateTime(year, 1, 1)));

        // MLK Day - 3rd Monday in January
        holidays.Add(NthWeekdayOfMonth(year, 1, DayOfWeek.Monday, 3));

        // Presidents' Day - 3rd Monday in February
        holidays.Add(NthWeekdayOfMonth(year, 2, DayOfWeek.Monday, 3));

        // Good Friday - 2 days before Easter Sunday
        holidays.Add(ComputeEasterSunday(year).AddDays(-2));

        // Memorial Day - Last Monday in May
        holidays.Add(LastWeekdayOfMonth(year, 5, DayOfWeek.Monday));

        // Juneteenth - June 19 (observed)
        holidays.Add(ObservedDate(new DateTime(year, 6, 19)));

        // Independence Day - July 4 (observed)
        holidays.Add(ObservedDate(new DateTime(year, 7, 4)));

        // Labor Day - 1st Monday in September
        holidays.Add(NthWeekdayOfMonth(year, 9, DayOfWeek.Monday, 1));

        // Thanksgiving - 4th Thursday in November
        holidays.Add(NthWeekdayOfMonth(year, 11, DayOfWeek.Thursday, 4));

        // Christmas - December 25 (observed)
        holidays.Add(ObservedDate(new DateTime(year, 12, 25)));

        return holidays;
    }

    /// <summary>
    /// If the holiday falls on Saturday, observe Friday. If Sunday, observe Monday.
    /// </summary>
    private static DateTime ObservedDate(DateTime holiday)
    {
        return holiday.DayOfWeek switch
        {
            DayOfWeek.Saturday => holiday.AddDays(-1),
            DayOfWeek.Sunday => holiday.AddDays(1),
            _ => holiday
        };
    }

    private static DateTime NthWeekdayOfMonth(int year, int month, DayOfWeek day, int n)
    {
        var first = new DateTime(year, month, 1);
        var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    private static DateTime LastWeekdayOfMonth(int year, int month, DayOfWeek day)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)day + 7) % 7;
        return last.AddDays(-offset);
    }

    /// <summary>
    /// Compute Easter Sunday using the Anonymous Gregorian algorithm.
    /// </summary>
    private static DateTime ComputeEasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }
}
