using System.Text.RegularExpressions;
using CadToCode.Models.Enums;

namespace CadToCode.Business.Services;

public static class ProfileKeywordResolver
{
    private static readonly (string Keyword, ComponentType Type)[] Keywords =
    {
        ("head", ComponentType.Head),
        ("jamb", ComponentType.Jamb),
        ("mullion", ComponentType.Mullion),
        ("sill", ComponentType.Sill),
        ("bracket", ComponentType.Bracket),
        ("flange", ComponentType.Flange),
        ("panel", ComponentType.Panel)
    };

    public static ComponentType? Resolve(IEnumerable<string?> sources)
    {
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var tokens = Regex.Split(source.ToLowerInvariant(), "[^a-z]+")
                .Where(t => t.Length > 0)
                .ToList();

            foreach (var token in tokens)
            {
                foreach (var (keyword, type) in Keywords)
                {
                    if (token == keyword || token == keyword + "s")
                    {
                        return type;
                    }
                }
            }
        }

        return null;
    }
}
