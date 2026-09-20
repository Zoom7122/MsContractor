namespace MergeVerifier.Models;

public sealed record VerificationReport(
    string SnapshotVersion,
    DateTimeOffset VerifiedAt,
    Guid AccountId,
    Guid MainCounterpartyId,
    CounterpartyVerification Counterparties,
    IReadOnlyList<DocumentVerification> Documents,
    IReadOnlyList<DocumentTypeSummary> Types,
    int Matched,
    int Missing,
    int Changed,
    int Ambiguous,
    int Unexpected)
{
    public bool Passed => Counterparties.MainExists && Counterparties.MainArchived == false &&
                          Counterparties.Duplicates.All(item => item.Passed) &&
                          Missing == 0 && Changed == 0 && Ambiguous == 0;
}

public sealed record CounterpartyVerification(bool MainExists, bool? MainArchived,
    IReadOnlyList<DuplicateCounterpartyVerification> Duplicates, IReadOnlyList<CounterpartyFieldChange> MainChanges);
public sealed record DuplicateCounterpartyVerification(Guid CounterpartyId, bool Exists, bool? Archived, bool Passed);
public sealed record CounterpartyFieldChange(string Field, string? Before, string? After);
public sealed record DocumentVerification(string DocumentType, Guid BeforeDocumentId, Guid? AfterDocumentId,
    string Operation, string Status, string? Detail = null);
public sealed record DocumentTypeSummary(string DocumentType, int BeforeMain, int BeforeDuplicates, int AfterMain,
    int Matched, int Missing, int Changed, int Ambiguous, int Unexpected);
