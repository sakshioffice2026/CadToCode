using CadToCode.Database;
using CadToCode.Database.Entities;
using CadToCode.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CadToCode.Repositories.Implementations;

public class UserRepository : IUserRepository
{
    private readonly ApplicationDbContext _db;

    public UserRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public Task<User?> GetByIdAsync(int id)
    {
        return _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }

    public Task<User?> GetByEmailAsync(string email)
    {
        return _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
    }

    public Task<bool> EmailExistsAsync(string email)
    {
        return _db.Users.AnyAsync(u => u.Email == email);
    }

    public Task<bool> UserNameExistsAsync(string userName)
    {
        return _db.Users.AnyAsync(u => u.UserName == userName);
    }

    public Task<bool> AnyUserExistsAsync()
    {
        return _db.Users.AnyAsync();
    }

    public async Task<User> AddAsync(User user)
    {
        user.CreatedAt = DateTime.UtcNow;
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task UpdateLastLoginAsync(int id)
    {
        var entity = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (entity == null) return;

        entity.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }
}