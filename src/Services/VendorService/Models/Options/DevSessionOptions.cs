using Microsoft.Extensions.Options;

namespace MsContractor.VendorService.Models.Options;

public sealed class DevSessionOptions
{
    public const string SectionName = "DevSession";

    public Guid AccountId { get; init; }
}

public sealed class DevSessionOptionsValidator : IValidateOptions<DevSessionOptions>
{
    public ValidateOptionsResult Validate(string? name, DevSessionOptions options) =>
        options.AccountId == Guid.Empty
            ? ValidateOptionsResult.Fail("Dev session account ID must be a non-empty UUID.")
            : ValidateOptionsResult.Success;
}
