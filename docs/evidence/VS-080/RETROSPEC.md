# VS-080 RetroSpec

## Estado sincronizado

La implementación confirma la spec de orquestación editorial con estos refinamientos:

- el plan usa un DAG canónico de ocho pasadas profesionales;
- cada transición exige revisión esperada y actor;
- una pasada solo puede comenzar cuando dependencias y gates previos están satisfechos;
- un cambio en la autoridad global puede marcar el plan como stale;
- replay exacto no duplica intentos, gates, historial ni eventos;
- la durabilidad se verifica tras reinicio y con aislamiento por workspace.

No se requieren cambios adicionales en la intención ni en los invariantes principales.
