var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet(
    "/health",
    () => Results.Ok(new { status = "ok", service = "BookStudio.ControlCenter" }));

app.Run();
