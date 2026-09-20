using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Services.Documents;

namespace MsContractor.Sync.Tests;

internal sealed class TestMergeVerificationSnapshotService : IMoySkladMergeVerificationSnapshotService
{
    public Task<MoySkladMergeVerificationSnapshotResponse> CaptureAsync(
        Guid accountId,
        IReadOnlyList<Guid> counterpartyIds,
        string correlationId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new MoySkladMergeVerificationSnapshotResponse([], [], []));
}
