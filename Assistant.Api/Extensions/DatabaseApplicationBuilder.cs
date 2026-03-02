using Assistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Extensions;

public static class DatabaseApplicationBuilder
{
    public static async Task UseDatabaseMigrationsAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        if (app.Environment.IsDevelopment())
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
