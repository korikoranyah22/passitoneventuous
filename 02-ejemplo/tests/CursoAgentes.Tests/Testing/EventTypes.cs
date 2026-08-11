using CursoAgentes.Domain.Workflow;
using Eventuous;

namespace CursoAgentes.Tests.Testing;

/// <summary>
/// Registra los tipos de eventos del dominio en el TypeMap de Eventuous
/// (necesario para que los command services sepan serializar/deserializar).
/// Idempotente: se puede llamar muchas veces.
/// </summary>
public static class EventTypes
{
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        TypeMap.RegisterKnownEventTypes(typeof(WorkflowRunEvents.V1.WorkflowRunCreated).Assembly);
        _registered = true;
    }
}
