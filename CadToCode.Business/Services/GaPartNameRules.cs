using System.Text.RegularExpressions;

namespace CadToCode.Business.Services
{
    public static class GaPartNameRules
    {
        private static readonly Regex ConsumableRx = new(
            @"\b(?:screw|stud|rivet|pin|cotter|circlip|o[\s\-]?ring|oil\s?seal|grease|paint|primer|sealant|adhesive|tape|rope|cable|spring\s?washer|plain\s?washer|lock\s?washer|lock\s?nut|anchor|fastener|eye\s?bolt|u[\s\-]?bolt|grub|key|woodruff|bush|sleeve|gland|packing|rubber|neoprene|felt|foam|insulation|nameplate|label)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsConsumable(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return ConsumableRx.IsMatch(name);
        }
    }
}
