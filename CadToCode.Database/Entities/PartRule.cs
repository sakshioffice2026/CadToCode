using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("part_rules")]
public class PartRule
{
    [Key, Column("rule_key")] public string RuleKey { get; set; } = "";
    [Column("display_name")] public string? DisplayName { get; set; }
    [Column("include_flag")] public bool Include { get; set; } = true;
    [Column("merge_into")] public string? MergeInto { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}