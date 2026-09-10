# Paso 14 · Auditoría defensiva de sistemas electorales

## Objetivo

Traducir la idea de una "IA inversa" defensiva a un workflow concreto y
auditable, sin convertir al modelo en autoridad de seguridad ni enseñar fases
de ataque.

El ejemplo usa telemetría completamente sintética y compara tres formas de
expresar la misma misión:

- pipeline secuencial normal;
- grafo de nodos fijo y no recursivo;
- grafo con objetivos recursivos acotados.

Código ejecutable:
[`MiyuAgents/examples/election-defense`](../../angelnairav2_public/Packages/MiyuAgents/examples/election-defense/).

## La frontera importante

La IA no "defiende la elección" por sí sola. Sus responsabilidades son mucho
más estrechas:

| Parte | Naturaleza | Responsabilidad |
|---|---|---|
| Recolección | I/O controlado | Obtener telemetría de fuentes permitidas |
| Validación | Determinista | Rechazar registros incompletos, duplicados o fuera de ventana |
| Detección | Determinista | Producir findings reproducibles con IDs de evidencia |
| Evaluación | Agéntica | Proponer hipótesis limitadas a esos findings |
| Crítica | Agéntica | Buscar afirmaciones sin respaldo y evidencia faltante |
| Validación de citas | Determinista | Impedir que el LLM invente un finding |
| Decisión | Determinista | Aplicar una política versionada |
| Efecto | Determinista e idempotente | Abrir un caso para revisión humana una sola vez |

La política siempre fija:

```text
RequiresHumanApproval = true
AutomaticSystemMutationAllowed = false
```

Por lo tanto, ni una evaluación muy confiada ni el acuerdo de dos modelos puede
aislar equipos, cambiar software o alterar datos electorales.

## Señales del ejemplo

El detector busca cuatro clases de evidencia defensiva:

1. digest de software distinto de la baseline aprobada;
2. acceso privilegiado exitoso fuera del segmento administrativo;
3. cinco o más autenticaciones fallidas sobre un mismo principal;
4. huecos en una secuencia append-only de auditoría.

Cada finding conserva los IDs de los registros que lo originaron. El LLM recibe
findings normalizados, no libertad para declarar que "vio" eventos inexistentes.

## Variante A · Pipeline normal

La variante `pipeline` tiene ocho etapas ordenadas. Es la forma más directa
cuando el orden es conocido y cada etapa se ejecuta una vez.

```powershell
dotnet run --project ../angelnairav2_public/Packages/MiyuAgents/examples/election-defense -- pipeline
```

El programa repite el mismo `runId`: la segunda ejecución recupera el recibo y
no crea otro caso. Esa idempotencia no depende del LLM.

## Variante B · Nodos fijos

La variante `nodes` convierte cada etapa en un `INodeAgent` y las reúne con
`SequenceStrategy`:

```powershell
dotnet run --project ../angelnairav2_public/Packages/MiyuAgents/examples/election-defense -- nodes
```

El comportamiento sigue siendo lineal y no recursivo. Elegir nodos se justifica
por composición, artifacts y trazabilidad, no porque todo workflow de agentes
deba recursar.

## Variante C · Objetivos recursivos

La variante `recursive` recursa solamente tres objetivos:

1. recolectar hasta cubrir integridad, acceso, autenticación y continuidad;
2. revisar la evaluación hasta que cite todos los findings;
3. repetir la crítica hasta resolver faltantes, afirmaciones sin respaldo y el
   umbral de confianza.

```powershell
dotnet run --project ../angelnairav2_public/Packages/MiyuAgents/examples/election-defense -- recursive
```

Cada objetivo registra cuántos refinamientos necesitó y corta por profundidad,
cantidad de llamadas, duración o ciclo. La decisión y el efecto final continúan
siendo nodos normales: no ganan nada por recursar.

## Routing privado

Los gateways offline anuncian dos perfiles:

- `private,triage,structured-output`;
- `private,critic,reasoning,structured-output`.

En un laboratorio local pueden reemplazarse por dos rutas Ollama, por ejemplo
un modelo pequeño para triage y `qwen3:4b-thinking` o `deepseek-r1:8b` para la
crítica. El nombre de proveedor no concede acceso a datos: privacidad,
residencia, sanitización y autorización son políticas explícitas del host.

## Extensión event-sourced

Si este proceso debe sobrevivir reinicios, persistí como eventos:

- ventana y hash del lote recolectado;
- findings deterministas y sus IDs de evidencia;
- ruta/modelo efectivos de cada llamada;
- evaluaciones rechazadas y criterio incumplido;
- versión de política y decisión;
- intención y recibo del caso idempotente.

No hace falta persistir razonamiento privado del proveedor. Sí hace falta poder
reconstruir qué evidencia, contrato y política produjeron el resultado.

## Límites del ejemplo

No es una certificación electoral ni una implementación de una norma de
seguridad. No contiene infraestructura real, credenciales, escaneo activo,
exploits, malware ni acciones de contención. Su propósito es enseñar cómo
encapsular análisis no determinista dentro de controles verificables y humanos.
