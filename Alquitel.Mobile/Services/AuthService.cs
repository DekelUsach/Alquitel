using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Mobile.Data;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.Services;

/// <summary>Login multiusuario contra la tabla Users compartida (mismo esquema que el desktop).</summary>
public class AuthService
{
    private readonly IDbContextFactory<MobileDbContext> _factory;

    public AuthService(IDbContextFactory<MobileDbContext> factory) => _factory = factory;

    public async Task<List<User>> GetUsersAsync()
    {
        using var db = _factory.CreateDbContext();
        return await db.Users
            .Where(u => !u.IsArchived)
            .OrderBy(u => u.Name)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <summary>
    /// Valida credenciales. Usuarios sin PasswordHash entran sin contraseña
    /// (mismo comportamiento que el LoginWindow del desktop).
    /// </summary>
    public async Task<User?> LoginAsync(Guid userId, string password)
    {
        using var db = _factory.CreateDbContext();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && !u.IsArchived);
        if (user == null) return null;

        if (string.IsNullOrWhiteSpace(user.PasswordHash)) return user;
        return PasswordHasher.Verify(password, user.PasswordHash) ? user : null;
    }
}
