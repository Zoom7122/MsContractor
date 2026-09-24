namespace DocumentRelationsGenerator.Randomization;

/// <summary>
/// Seeded source of valid business values. <see cref="Random(int)"/> with an explicit seed is
/// deterministic, so a failed scenario can be reproduced with the same --seed and --anchor-date.
/// </summary>
public sealed class TestDataRandomizer
{
    public static readonly IReadOnlyList<decimal> IntegerQuantities = [1m, 2m, 3m, 5m, 10m];
    public static readonly IReadOnlyList<decimal> FractionalQuantities = [0.5m, 1.5m, 2.5m];
    public static readonly IReadOnlyList<int> Discounts = [0, 5, 10, 15, 20];

    private static readonly string[] Descriptions =
    [
        "Плановая поставка по заявке менеджера",
        "Срочный заказ, согласовано по телефону",
        "Повторная закупка по прайсу текущего месяца",
        "Отгрузка со склада, самовывоз",
        "Доставка курьером до двери",
        "Оплата по счёту в течение 5 банковских дней",
        "Частичная оплата, остаток по графику",
        "Возврат по претензии покупателя",
        "Корректировка по акту сверки",
        "Сезонная партия, скидка по договору"
    ];

    private static readonly string[] PaymentPurposes =
    [
        "Оплата по счёту", "Предоплата за товар", "Доплата по договору", "Возврат денежных средств",
        "Оплата поставки", "Расчёт по отчёту комиссионера"
    ];

    private readonly Random random;

    public TestDataRandomizer(int seed)
    {
        random = new Random(seed);
    }

    public static TestDataRandomizer For(int runSeed, params object[] parts) => new(StableSeed.Derive(runSeed, parts));

    public int Between(int minInclusive, int maxInclusive) => random.Next(minInclusive, maxInclusive + 1);

    public bool Chance(double probability) => random.NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items) =>
        items.Count == 0 ? throw new InvalidOperationException("Nothing to pick from.") : items[random.Next(items.Count)];

    /// <summary>Realistic retail price in kopecks (MoySklad stores money in kopecks): 100 ₽, 249.90 ₽, 1299 ₽, 8450 ₽.</summary>
    public long NextPriceKopecks()
    {
        switch (Between(0, 2))
        {
            case 0: // 50–499 ₽ with price-list kopecks: 249.90, 100.00, 75.50
                return Between(50, 499) * 100L + Pick(new[] { 0, 90, 50 });
            case 1: // 500–2999 ₽ rounded to tens, often "-1": 1299, 1300
                var mid = Between(50, 299) * 10 - (Chance(0.5) ? 1 : 0);
                return mid * 100L;
            default: // 3000–15000 ₽ rounded to 50: 8450
                return Between(60, 300) * 50 * 100L;
        }
    }

    public decimal NextQuantity(bool allowFractional) =>
        allowFractional && Chance(0.25) ? Pick(FractionalQuantities) : Pick(IntegerQuantities);

    public int NextDiscount() => Pick(Discounts);

    /// <summary>Returned or written-off quantity: never above the base, never zero.</summary>
    public decimal ReduceQuantity(decimal available)
    {
        if (available <= 0) throw new ArgumentOutOfRangeException(nameof(available));
        if (available <= 1 || Chance(0.4)) return available;
        var whole = (int)Math.Floor(available);
        return Between(1, whole);
    }

    /// <summary>Share of an amount for partial payments; 1.0 means full payment.</summary>
    public decimal NextPaymentShare() => Chance(0.5) ? 1m : Between(30, 90) / 100m;

    /// <summary>Scenario start: 20–200 days before the anchor date, during working hours.</summary>
    public DateTime NextScenarioStart(DateOnly anchor) =>
        anchor.ToDateTime(new TimeOnly(Between(9, 17), Between(0, 59))).AddDays(-Between(20, 200));

    /// <summary>Next business event after <paramref name="previous"/>: minutes to a few days later.</summary>
    public DateTime NextMomentAfter(DateTime previous) =>
        Chance(0.5) ? previous.AddMinutes(Between(5, 240)) : previous.AddDays(Between(1, 4)).AddMinutes(Between(0, 120));

    public string NextDescription() => Pick(Descriptions);

    public string NextPaymentPurpose() => Pick(PaymentPurposes);

    public string NextDigits(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)('0' + random.Next(10));
        if (chars[0] == '0') chars[0] = '1';
        return new string(chars);
    }
}
