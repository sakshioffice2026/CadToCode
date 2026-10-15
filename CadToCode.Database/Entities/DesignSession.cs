using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("DesignSessions")]
public class DesignSession
{
    [Key, Column("Id")]
    public Guid Id { get; set; }

    [Required, MaxLength(100), Column("UserId")]
    public string UserId { get; set; } = string.Empty;

    [Required, MaxLength(40), Column("EquipmentType")]
    public string EquipmentType { get; set; } = string.Empty;

    [Required, MaxLength(40), Column("State")]
    public string State { get; set; } = string.Empty;

    [Column("InputsJson", TypeName = "longtext")]
    public string? InputsJson { get; set; }

    [Column("ResultJson", TypeName = "longtext")]
    public string? ResultJson { get; set; }

    [MaxLength(1000), Column("RejectionReason")]
    public string? RejectionReason { get; set; }

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("UpdatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}