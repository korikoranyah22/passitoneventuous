# Investigación y supuestos para las elecciones nacionales 2027

Fecha de corte de esta investigación: **10 de septiembre de 2026**.

## Qué sabemos

1. El Código Electoral Nacional establece que la elección nacional se realiza
   el cuarto domingo de octubre anterior al fin de los mandatos. Aplicado a
   2027, eso permite proyectar el **24 de octubre de 2027**, pero esta fecha no
   reemplaza el cronograma que publique la Cámara Nacional Electoral.
2. Por el ciclo constitucional, corresponde contemplar presidente y
   vicepresidente, la renovación de la mitad de la Cámara de Diputados y la de
   un tercio de los distritos del Senado. El ejemplo no fija el reparto de
   bancas ni los distritos senatoriales: deben venir de la convocatoria oficial.
3. La Ley 27.781 estableció la Boleta Única de Papel para los procesos
   electorales nacionales. Estos ejemplos no modelan la emisión electrónica del
   voto porque el instrumento nacional es papel.
4. Argentina mantiene dos procesos distintos: el recuento provisorio de la
   noche electoral, basado en telegramas, y el escrutinio definitivo realizado
   por la Justicia Nacional Electoral a partir de la documentación electoral.
   Una alerta del provisorio no determina la validez jurídica de un resultado.
5. La suspensión de las PASO dispuesta por la Ley 27.783 alcanzó únicamente a
   2025. Por eso el software no fija una primaria 2027: recibe las fases desde
   `IElectionCalendarPort`.
6. El simulacro nacional de 2025 reprodujo transmisión, recepción, carga,
   procesamiento, fiscalización, totalización y difusión. Incluyó más de 108 mil
   telegramas, kits de transmisión en más de 14 mil locales y sucursales
   electorales digitales.
7. Los fiscales informáticos monitorean el flujo, el estado de las mesas y las
   imágenes y datos cargados para el provisorio. Esto justifica un puerto de
   observación para fiscalización, no acceso directo a bases productivas.

## Fuentes oficiales

- [Código Electoral Nacional actualizado](https://www.argentina.gob.ar/normativa/nacional/ley-19945-19442/actualizacion).
- [Constitución Nacional: duración y renovación de autoridades](https://www.argentina.gob.ar/normativa/nacional/804/texto).
- [Ley 27.781 · Boleta Única de Papel](https://www.boletinoficial.gob.ar/detalleAviso/primera/315713/20241018).
- [Ley 27.783 · suspensión de PASO sólo durante 2025](https://www.argentina.gob.ar/normativa/nacional/ley-27783-410378/texto).
- [Sistema electoral argentino: recuento provisorio y definitivo](https://www.argentina.gob.ar/sites/default/files/2024/08/libro_sistema_electoral_argentino.pdf).
- [Simulacro general de transmisión y recuento 2025](https://www.argentina.gob.ar/node/482929).
- [Manual 2025 para fiscales, incluidos fiscales informáticos](https://www.argentina.gob.ar/sites/default/files/manual_fiscales_2025_web.pdf).
- [Dirección de Innovación y Tecnología Electoral](https://www.argentina.gob.ar/dine/direccion-de-innovacion-y-tecnologia-electoral).

## Qué suponemos

No hay documentación pública que garantice estas APIs para 2027. Los ejemplos
suponen que un entorno autorizado puede exponer adaptadores de sólo lectura:

| Puerto hipotético | Datos mínimos | Lo que no concede |
|---|---|---|
| `IElectionCalendarPort` | contiendas, fases, ventanas y carácter oficial/proyectado | cambiar el cronograma o reparto de bancas |
| `ISoftwareBaselinePort` | releases aprobadas y mediciones de digest | desplegar o reemplazar software |
| `ITransmissionObservationPort` | envío/recepción y digest del sobre digital | leer o modificar el contenido electoral |
| `IDigitizationAuditPort` | doble carga, digest de imagen y visibilidad fiscal | corregir resultados |
| `IAccessAuditPort` | accesos administrativos seudonimizados | credenciales o sesiones interactivas |
| `IPublicationCheckpointPort` | secuencia, cantidad incluida y digest del dataset | publicar resultados |
| `IHumanReviewCasePort` | alta idempotente de una revisión | contención automática |

Los adaptadores reales deberían aplicar autenticación mutua, mínimo privilegio,
retención, seudonimización y logs inmutables. Esas propiedades pertenecen al
host y a la autoridad electoral; no se infieren de una marca de LLM.

## Decisiones de alcance

- Los ejemplos observan sistemas auxiliares del **recuento provisorio** y su
  fiscalización.
- No procesan elecciones partidarias, preferencias individuales ni padrones.
- No reemplazan el escrutinio definitivo.
- No contienen escaneo activo, payloads, explotación ni respuesta ofensiva.
- El LLM propone y critica hipótesis; reglas deterministas validan evidencia y
  abren un caso para personas autorizadas.
