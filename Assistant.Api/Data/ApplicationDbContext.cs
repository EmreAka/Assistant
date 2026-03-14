using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Expense.Models;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<TelegramUser> TelegramUsers { get; set; }
    public DbSet<AssistantPersonality> AssistantPersonalities { get; set; }
    public DbSet<Expense> Expenses { get; set; }
    public DbSet<UserMemory> UserMemories { get; set; }
    public DbSet<DeferredIntent> DeferredIntents { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
