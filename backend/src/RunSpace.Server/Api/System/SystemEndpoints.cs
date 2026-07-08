using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/ping", () =>
            Results.Ok(new
            {
                status = "ok",
                time = DateTime.UtcNow.ToString("o")
            }));
    }
}
