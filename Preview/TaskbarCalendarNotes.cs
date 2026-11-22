using System.Globalization;

namespace AITaskbar;

internal static class TaskbarCalendarNotes
{
    internal readonly record struct Note(string Label, string Details, bool Emphasized);

    private enum Schedule { None, Rest, Work }

    private static readonly ChineseLunisolarCalendar Lunar = new();
    private static readonly string[] Months = ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];
    private static readonly string[] Days = ["初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"];

    internal static Note ForDate(DateTime date)
    {
        date = date.Date;
        string? solarFestival = (date.Month, date.Day) switch
        {
            (1, 1) => "元旦",
            (4, _) when date.Day == QingmingDay(date.Year) => "清明节",
            (5, 1) => "劳动节",
            (10, 1) => "国庆节",
            _ => null,
        };
        string? lunarFestival = null, lunarLabel = null, lunarDetails = null;
        if (date >= Lunar.MinSupportedDateTime && date <= Lunar.MaxSupportedDateTime)
        {
            int year = Lunar.GetYear(date), month = Lunar.GetMonth(date), day = Lunar.GetDayOfMonth(date);
            int leapMonth = Lunar.GetLeapMonth(year);
            bool isLeap = month == leapMonth;
            int monthNumber = leapMonth > 0 && month >= leapMonth ? month - 1 : month;
            string monthName = (isLeap ? "闰" : "") + Months[monthNumber - 1];
            lunarLabel = day == 1 ? monthName : Days[day - 1];
            lunarDetails = $"农历{monthName}{Days[day - 1]}";

            if (month == Lunar.GetMonthsInYear(year) && day == Lunar.GetDaysInMonth(year, month))
                lunarFestival = "除夕";
            else if (!isLeap)
                lunarFestival = (monthNumber, day) switch
                {
                    (1, 1) => "春节", (1, 15) => "元宵节", (5, 5) => "端午节", (7, 7) => "七夕",
                    (8, 15) => "中秋节", (9, 9) => "重阳节", (12, 8) => "腊八节", _ => null,
                };
        }

        string? festival = solarFestival ?? lunarFestival;
        Schedule schedule = GetSchedule(date);
        string? mark = schedule switch { Schedule.Rest => "休", Schedule.Work => "班", _ => null };
        string? festivalLabel = mark is not null && festival is { Length: > 2 } && festival.EndsWith('节')
            ? festival[..^1] : festival;
        string label = string.Join(" ", new[] { festivalLabel ?? lunarLabel, mark }.OfType<string>());
        string details = string.Join(" · ", new[] { lunarDetails, solarFestival, lunarFestival,
            schedule switch { Schedule.Rest => "放假", Schedule.Work => "调休上班", _ => null } }.OfType<string>());
        return new(label, details, festival is not null || mark is not null);
    }

    // China Standard Time dates, Central Weather Administration's published Qingming tables.
    private static int QingmingDay(int year) => year switch
    {
        >= 1912 and <= 1943 => year % 4 == 3 ? 6 : 5,
        >= 1944 and <= 1975 => 5,
        >= 1976 and <= 2007 => year % 4 == 0 ? 4 : 5,
        >= 2008 and <= 2039 => year % 4 < 2 ? 4 : 5,
        >= 2040 and <= 2071 => year % 4 == 3 ? 5 : 4,
        >= 2072 and <= 2099 => 4,
        2100 => 5,
        _ => 0,
    };

    // Annual schedules are published separately from festival dates.
    // 2026: 国办发明电〔2025〕7号. 2027: 国办发明电〔2026〕10号.
    private static Schedule GetSchedule(DateTime date) => (date.Year, date.Month, date.Day) switch
    {
        (2026, 1, <= 3) or (2026, 2, >= 15 and <= 23) or (2026, 4, >= 4 and <= 6)
            or (2026, 5, <= 5) or (2026, 6, >= 19 and <= 21) or (2026, 9, >= 25 and <= 27)
            or (2026, 10, <= 7) => Schedule.Rest,
        (2026, 1, 4) or (2026, 2, 14 or 28) or (2026, 5, 9) or (2026, 9, 20) or (2026, 10, 10) => Schedule.Work,
        (2027, 1, <= 3) or (2027, 2, >= 5 and <= 13) or (2027, 4, >= 3 and <= 5)
            or (2027, 5, <= 5) or (2027, 6, 9) or (2027, 9, 15) or (2027, 10, <= 7) => Schedule.Rest,
        (2027, 1, 31) or (2027, 2, 14) or (2027, 5, 8) or (2027, 9, 26) or (2027, 10, 9) => Schedule.Work,
        _ => Schedule.None,
    };
}
