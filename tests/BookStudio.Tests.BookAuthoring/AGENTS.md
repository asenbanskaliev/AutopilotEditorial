# BookStudio.Tests.BookAuthoring Agent Rules

- Launch the real `BookStudio.Mcp.Authoring.dll` process.
- Use only stdio JSON-RPC as product evidence.
- Do not invoke the router or Application service directly as the sole acceptance proof.
- Do not add fixed sleeps; chain requests and responses deterministically.
- Verify stdout contains protocol JSON only and stderr contains safe diagnostic codes only.
- Use a disposable workspace and verify lazy creation and immutable versions.
