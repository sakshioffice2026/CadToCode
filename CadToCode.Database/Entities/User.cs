using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("Users")]
public class User
{
    [Key, Column("Id")]
    public int Id { get; set; }

    [Required, MaxLength(100), Column("UserName")]
    public string UserName { get; set; } = string.Empty;

    [Required, MaxLength(256), Column("Email")]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(255), Column("PasswordHash")]
    public string PasswordHash { get; set; } = string.Empty;

    [Required, MaxLength(20), Column("Role")]
    public string Role { get; set; } = "User";

    [Column("IsActive")]
    public bool IsActive { get; set; } = true;

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("LastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    public ICollection<Project> Projects { get; set; } = new List<Project>();
}