using MsContractor.VendorService.Models.Options;

namespace MsContractor.VendorService.Tests;

public sealed class DevSessionOptionsTests
{
    [Fact]
    public void Validate_RejectsEmptyAccountId()
    {
        var result = new DevSessionOptionsValidator().Validate(null, new DevSessionOptions());

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_AcceptsConfiguredAccountId()
    {
        var result = new DevSessionOptionsValidator().Validate(
            null,
            new DevSessionOptions { AccountId = Guid.NewGuid() });

        Assert.True(result.Succeeded);
    }
}
