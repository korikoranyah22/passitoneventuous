using CursoAgentes.Domain.Incidents;
using CursoAgentes.Engine.Incidents;
using Eventuous.Subscriptions;
using EventHandler = Eventuous.Subscriptions.EventHandler;

namespace CursoAgentes.Infrastructure.Incidents;

/// <summary>
/// Reacciona a una decisión que ya fue confirmada por el event store. La
/// suscripción que hospeda este handler tiene su propio checkpoint durable:
/// sólo avanza después de ejecutar el puerto y persistir la confirmación.
/// </summary>
public sealed class IncidentActionReaction : EventHandler
{
    public IncidentActionReaction(IncidentActionReactionProcessor processor)
    {
        On<IncidentInvestigationEvents.V1.IncidentActionDecided>(context =>
            new ValueTask(processor.HandleAsync(
                context.Message,
                context.CancellationToken)));
        On<IncidentInvestigationEvents.V1.IncidentActionRetryRequested>(context =>
            new ValueTask(processor.HandleAsync(
                context.Message,
                context.CancellationToken)));
    }
}
