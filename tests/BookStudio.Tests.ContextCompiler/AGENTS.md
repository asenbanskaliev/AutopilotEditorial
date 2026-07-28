# Context Compiler Journey

## Allowed

- Ejecutar el journey acumulativo sin red ni mutaciones remotas.
- Validar orden determinista, presupuestos globales y por trust label, integridad SHA-256, fuentes requeridas, duplicados y cancelación.
- Añadir escenarios que conserven fingerprints reproducibles y comportamiento fail-closed.

## Forbidden

- Realizar llamadas de red, modificar recursos remotos o depender del orden de entrada.
- Debilitar límites, hashes, etiquetas de confianza o restricciones de fuentes requeridas para hacer pasar una prueba.
- Introducir paquetes externos o dependencias fuera de `BookStudio.Application`.

## Verification

- `dotnet run --project tests/BookStudio.Tests.ContextCompiler/BookStudio.Tests.ContextCompiler.csproj --no-build -c Release`
- La salida válida termina en `OPENCODE_CONTEXT_COMPILER_PASS` y `mutation=NONE`.
