namespace CursoAgentes.Domain.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// Tipos de estado compartidos por los aggregates del workflow recursivo.
// Un WorkflowRun es UNA ejecución completa del workflow (un objetivo raíz).
// Un WorkflowNode es UN nodo del árbol de ejecución (objetivo + resultado).
// ─────────────────────────────────────────────────────────────────────────────

public enum WorkflowRunStatus
{
    /// <summary>
    /// Estado por defecto de un aggregate recién instanciado (el run NO existe).
    /// Ningún guard de transición acepta None: así, un comando dirigido a un run
    /// inexistente es rechazado por el dominio y no "cuela" un estado válido.
    /// </summary>
    None = 0,

    /// <summary>El run fue creado y está ejecutándose (o esperando ejecución).</summary>
    Running,

    /// <summary>El run terminó bien: el nodo raíz produjo una respuesta final.</summary>
    Completed,

    /// <summary>El run falló (LLM inalcanzable, error de validación, etc.).</summary>
    Failed
}

public enum WorkflowNodeStatus
{
    /// <summary>
    /// Estado por defecto de un aggregate recién instanciado (el nodo NO existe).
    /// Ningún guard de transición acepta None: un comando dirigido a un nodo
    /// inexistente es rechazado por el dominio.
    /// </summary>
    None = 0,

    /// <summary>El nodo fue creado pero todavía no se decidió si es hoja o se divide.</summary>
    Pending,

    /// <summary>El nodo fue planificado (es hoja o tiene hijos).</summary>
    Planned,

    /// <summary>El nodo tiene una respuesta final (propia o sintetizada).</summary>
    Completed,

    /// <summary>El nodo falló y su respuesta no está disponible.</summary>
    Failed
}
