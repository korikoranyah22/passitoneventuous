using CursoAgentes.Domain.Incidents;
using Eventuous.Subscriptions;
using EventHandler = Eventuous.Subscriptions.EventHandler;

namespace CursoAgentes.Infrastructure.Projections;

public sealed class IncidentReadModelProjection : EventHandler
{
    public IncidentReadModelProjection(IncidentReadModelStore store)
    {
        On<IncidentInvestigationEvents.V1.IncidentInvestigationStarted>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentSignalRecorded>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentAnalysisRecorded>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentCritiqueRecorded>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentActionDecided>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentActionParked>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentActionRetryRequested>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentOpened>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentHumanReviewRequested>(context =>
            new ValueTask(store.UpsertAsync(context.Message, context.CancellationToken)));
    }
}
