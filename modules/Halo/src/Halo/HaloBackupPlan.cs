namespace Halo;

internal sealed record HaloBackupPlan(string Directory = "", bool Enabled = false, bool Weekly = true,
    DayOfWeek Day = DayOfWeek.Sunday, int Minute = 22 * 60, DateTimeOffset? ActivatedAt = null)
{
    internal void Validate()
    {
        if (Directory == null || Directory.Length > 32000 || Directory.Any(char.IsControl)
            || Directory.Length > 0 && !Path.IsPathFullyQualified(Directory)
            || Enabled && Directory.Length == 0 || Minute is < 0 or >= 1440 || !Enum.IsDefined(Day))
            throw new InvalidDataException("请选择有效的备份位置和时间。");
    }

    internal DateTime LatestSlot(DateTime local)
    {
        int days = Weekly ? (7 + (int)local.DayOfWeek - (int)Day) % 7 : 0;
        DateTime slot = local.Date.AddDays(-days).AddMinutes(Minute);
        return slot <= local ? slot : slot.AddDays(Weekly ? -7 : -1);
    }

    internal DateTime NextSlot(DateTime local) => LatestSlot(local).AddDays(Weekly ? 7 : 1);
}
