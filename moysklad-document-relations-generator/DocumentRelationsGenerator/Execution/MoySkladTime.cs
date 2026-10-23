using System.Globalization;

namespace DocumentRelationsGenerator.Execution;

public static class MoySkladTime
{
    /// <summary>
    /// "ГГГГ-ММ-ДД ЧЧ:мм:сс". moment, incomingDate and commission periods are stored with minute
    /// precision, so seconds are always zero.
    /// </summary>
    public static string Format(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:00", CultureInfo.InvariantCulture);
}
