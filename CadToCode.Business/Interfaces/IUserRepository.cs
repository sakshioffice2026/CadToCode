using CadToCode.Database.Entities;

namespace CadToCode.Repositories.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id);
    Task<User?> GetByEmailAsync(string email);
    Task<bool> EmailExistsAsync(string email);
    Task<bool> UserNameExistsAsync(string userName);
    Task<bool> AnyUserExistsAsync();
    Task<User> AddAsync(User user);
    Task UpdateLastLoginAsync(int id);
}