using Microsoft.EntityFrameworkCore;

namespace EasyHomeServer.Host.Data;

/// <summary>
/// The host's own SQLite store. Deliberately narrow: modules do not share this context, and
/// if a future module needs persistence it gets its own store under its data directory.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Persisted host settings.</summary>
    public DbSet<Setting> Settings => Set<Setting>();
}
