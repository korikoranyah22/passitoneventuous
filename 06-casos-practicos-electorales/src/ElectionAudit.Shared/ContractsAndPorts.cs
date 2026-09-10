namespace PassItOn.ElectionAudit.Shared;

public enum CalendarAuthority
{
    ProjectedFromCurrentLaw,
    Official
}

public sealed record ElectionPhaseWindow(
    string Phase,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

public sealed record ElectionContest(
    string Office,
    string Scope,
    string Renewal,
    string AuthorityNote);

public sealed record ElectionCalendar(
    string ElectionId,
    DateOnly GeneralElectionDate,
    CalendarAuthority Authority,
    string SourceReference,
    string VotingInstrument,
    IReadOnlyList<ElectionPhaseWindow> ObservablePhases,
    IReadOnlyList<ElectionContest> Contests);

public sealed record SoftwareBaseline(
    string Component,
    string ApprovedVersion,
    string ApprovedDigest,
    DateTimeOffset ApprovedAt);

public sealed record RuntimeMeasurement(
    string Id,
    string Component,
    string NodeId,
    string RunningVersion,
    string ObservedDigest,
    DateTimeOffset ObservedAt);

public sealed record TransmissionObservation(
    string Id,
    string TelegramId,
    string PollingPlaceId,
    string Channel,
    DateTimeOffset SentAt,
    DateTimeOffset? ReceivedAt,
    string SentEnvelopeDigest,
    string? ReceivedEnvelopeDigest);

public sealed record DigitizationAuditObservation(
    string Id,
    string TelegramId,
    string ImageDigest,
    string FirstEntryDigest,
    string SecondEntryDigest,
    bool VisibleToFiscalization,
    DateTimeOffset ProcessedAt);

public sealed record AccessAuditObservation(
    string Id,
    string PseudonymousPrincipal,
    string Role,
    string Resource,
    string SourceZone,
    bool Succeeded,
    DateTimeOffset ObservedAt);

public sealed record PublicationCheckpoint(
    string Id,
    long Sequence,
    int IncludedTelegrams,
    string DatasetDigest,
    DateTimeOffset PublishedAt);

public interface IElectionCalendarPort
{
    Task<ElectionCalendar> ReadAsync(CancellationToken ct);
}

public interface ISoftwareBaselinePort
{
    Task<IReadOnlyList<SoftwareBaseline>> ReadBaselinesAsync(CancellationToken ct);
    Task<IReadOnlyList<RuntimeMeasurement>> ReadMeasurementsAsync(CancellationToken ct);
}

public interface ITransmissionObservationPort
{
    Task<IReadOnlyList<TransmissionObservation>> ReadAsync(CancellationToken ct);
}

public interface IDigitizationAuditPort
{
    Task<IReadOnlyList<DigitizationAuditObservation>> ReadAsync(CancellationToken ct);
}

public interface IAccessAuditPort
{
    Task<IReadOnlyList<AccessAuditObservation>> ReadAsync(CancellationToken ct);
}

public interface IPublicationCheckpointPort
{
    Task<IReadOnlyList<PublicationCheckpoint>> ReadAsync(CancellationToken ct);
}

public sealed record ElectionObservationBundle(
    ElectionCalendar Calendar,
    IReadOnlyList<SoftwareBaseline> Baselines,
    IReadOnlyList<RuntimeMeasurement> Measurements,
    IReadOnlyList<TransmissionObservation> Transmissions,
    IReadOnlyList<DigitizationAuditObservation> Digitization,
    IReadOnlyList<AccessAuditObservation> Accesses,
    IReadOnlyList<PublicationCheckpoint> Publication);

public sealed class ElectionObservationCollector(
    IElectionCalendarPort calendar,
    ISoftwareBaselinePort software,
    ITransmissionObservationPort transmission,
    IDigitizationAuditPort digitization,
    IAccessAuditPort access,
    IPublicationCheckpointPort publication)
{
    public async Task<ElectionObservationBundle> CollectAsync(CancellationToken ct)
    {
        var calendarTask = calendar.ReadAsync(ct);
        var baselinesTask = software.ReadBaselinesAsync(ct);
        var measurementsTask = software.ReadMeasurementsAsync(ct);
        var transmissionTask = transmission.ReadAsync(ct);
        var digitizationTask = digitization.ReadAsync(ct);
        var accessTask = access.ReadAsync(ct);
        var publicationTask = publication.ReadAsync(ct);

        await Task.WhenAll(
            calendarTask,
            baselinesTask,
            measurementsTask,
            transmissionTask,
            digitizationTask,
            accessTask,
            publicationTask);

        return new ElectionObservationBundle(
            await calendarTask,
            await baselinesTask,
            await measurementsTask,
            await transmissionTask,
            await digitizationTask,
            await accessTask,
            await publicationTask);
    }
}

public static class AuditFindingCategories
{
    public const string ReleaseIntegrity = "release-integrity";
    public const string TransmissionIntegrity = "transmission-integrity";
    public const string TransmissionAvailability = "transmission-availability";
    public const string DigitizationConsistency = "digitization-consistency";
    public const string FiscalizationVisibility = "fiscalization-visibility";
    public const string PrivilegedAccess = "privileged-access";
    public const string PublicationMonotonicity = "publication-monotonicity";
}

public sealed record AuditFinding(
    string Id,
    string Category,
    string Severity,
    string Summary,
    IReadOnlyList<string> EvidenceIds);

public sealed record ElectionAuditSignal(
    string ElectionId,
    DateOnly GeneralElectionDate,
    CalendarAuthority CalendarAuthority,
    IReadOnlyList<AuditFinding> Findings);

public sealed record AuditAssessment(
    string Summary,
    IReadOnlyList<string> Hypotheses,
    double Confidence,
    IReadOnlyList<string> CitedFindingIds,
    IReadOnlyList<string> RequestedEvidence);

public sealed record AuditCritique(
    string Verdict,
    double Confidence,
    IReadOnlyList<string> UnsupportedClaims,
    IReadOnlyList<string> MissingEvidence,
    IReadOnlyList<string> CitedFindingIds);

public sealed record AuditDecision(
    string Action,
    string PolicyVersion,
    IReadOnlyList<string> Reasons,
    bool RequiresHumanApproval,
    bool AutomaticMutationAllowed,
    string LegalScope);

public sealed record HumanReviewReceipt(
    string IdempotencyKey,
    string CaseId,
    string Action,
    bool WasAlreadyCreated);

public interface IHumanReviewCasePort
{
    Task<HumanReviewReceipt> OpenOnceAsync(
        string idempotencyKey,
        AuditDecision decision,
        CancellationToken ct);
}

public sealed class InMemoryHumanReviewCasePort : IHumanReviewCasePort
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HumanReviewReceipt> _receipts =
        new(StringComparer.Ordinal);

    public int CreationCount { get; private set; }

    public Task<HumanReviewReceipt> OpenOnceAsync(
        string idempotencyKey,
        AuditDecision decision,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_receipts.TryGetValue(idempotencyKey, out var existing))
                return Task.FromResult(existing with { WasAlreadyCreated = true });

            var suffix = idempotencyKey[(idempotencyKey.LastIndexOf(':') + 1)..];
            var receipt = new HumanReviewReceipt(
                idempotencyKey,
                $"fiscalization-case-{suffix}",
                decision.Action,
                WasAlreadyCreated: false);
            _receipts.Add(idempotencyKey, receipt);
            CreationCount++;
            return Task.FromResult(receipt);
        }
    }
}

public static class ElectionAuditArtifacts
{
    public const string Observations = "election-observations";
    public const string Signal = "election-audit-signal";
    public const string Assessment = "election-audit-assessment";
    public const string Critique = "election-audit-critique";
    public const string Decision = "election-audit-decision";
    public const string Receipt = "human-review-receipt";
    public const string RecursionAudit = "recursion-audit";
}

public sealed record RecursionAudit(string Objective, int Refinements);

public sealed class ElectionAuditContractException(string message) : Exception(message);
