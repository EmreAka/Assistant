using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Expense.Models;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<TelegramUser> TelegramUsers { get; set; }
    public DbSet<AssistantPersonality> AssistantPersonalities { get; set; }
    public DbSet<ChatTurn> ChatTurns { get; set; }
    public DbSet<Expense> Expenses { get; set; }
    public DbSet<UserMemoryManifest> UserMemoryManifests { get; set; }
    public DbSet<DeferredIntent> DeferredIntents { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        if (!string.Equals(Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            modelBuilder.Entity<ChatTurn>().Ignore(x => x.SearchVector);
        }
    }
}
