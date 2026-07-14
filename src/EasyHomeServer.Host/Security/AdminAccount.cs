using EasyHomeServer.Host.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyHomeServer.Host.Security;

/// <summary>
/// The single admin account. There is one user; this wraps reading and writing its password
/// hash rather than pulling in ASP.NET Core Identity for one credential.
/// </summary>
internal sealed class AdminAccount(IDbContextFactory<AppDbContext> dbContextFactory)
{
    private const string PasswordKey = "admin.password";

    /// <summary>Name the admin is signed in as. Fixed: the tool is single-user.</summary>
    public const string UserName = "admin";

    /// <summary>
    /// True once a password exists. While false the host redirects to first-run setup rather
    /// than shipping a default password, so a fresh install is never reachable with a
    /// guessable credential.
    /// </summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        return await ReadHashAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Verifies a password attempt. Returns false when no password is configured.</summary>
    public async Task<bool> VerifyAsync(string password, CancellationToken cancellationToken = default)
    {
        var hash = await ReadHashAsync(cancellationToken).ConfigureAwait(false);

        if (hash is null)
        {
            return false;
        }

        return PasswordHasher.Verify(password, hash);
    }

    /// <summary>Sets or replaces the admin password.</summary>
    public async Task SetPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.Settings
            .FirstOrDefaultAsync(s => s.Key == PasswordKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.Settings.Add(new Setting
            {
                Key = PasswordKey,
                Value = PasswordHasher.Hash(password),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Value = PasswordHasher.Hash(password);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadHashAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var setting = await db.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == PasswordKey, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrEmpty(setting?.Value) ? null : setting.Value;
    }
}
