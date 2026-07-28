namespace Cs2Highlight.Analysis;

public interface IWeaponCatalog
{
    WeaponMetadata Resolve(string? weaponCode);
    string Canonicalize(string? weaponCode);
}

public sealed class WeaponCatalog : IWeaponCatalog
{
    private static readonly WeaponMetadata Unknown =
        new("unknown", "Unknown", "/assets/weapons/unknown.svg", WeaponCategory.Unknown);

    private static readonly Dictionary<string, WeaponMetadata> Weapons =
        new Dictionary<string, WeaponMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["ak47"] = Meta("ak47", "AK-47", WeaponCategory.Rifle),
            ["m4a1"] = Meta("m4a1", "M4A4", WeaponCategory.Rifle),
            ["m4a1_silencer"] = Meta("m4a1_silencer", "M4A1-S", WeaponCategory.Rifle),
            ["awp"] = Meta("awp", "AWP", WeaponCategory.Sniper),
            ["ssg08"] = Meta("ssg08", "SSG 08", WeaponCategory.Sniper),
            ["deagle"] = Meta("deagle", "Desert Eagle", WeaponCategory.Pistol),
            ["glock"] = Meta("glock", "Glock-18", WeaponCategory.Pistol),
            ["usp_silencer"] = Meta("usp_silencer", "USP-S", WeaponCategory.Pistol),
            ["knife"] = Meta("knife", "Knife", WeaponCategory.Knife),
            ["taser"] = Meta("taser", "Zeus x27", WeaponCategory.Equipment)
        };

    private static readonly Dictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ak-47"] = "ak47",
            ["m4a1"] = "m4a1",
            ["m4a4"] = "m4a1",
            ["m4a1-s"] = "m4a1_silencer",
            ["m4a1_silencer"] = "m4a1_silencer",
            ["desert eagle"] = "deagle",
            ["glock-18"] = "glock",
            ["usp-s"] = "usp_silencer",
            ["ssg 08"] = "ssg08",
            ["zeus x27"] = "taser",
            ["zeus"] = "taser"
        };

    public WeaponMetadata Resolve(string? weaponCode) =>
        Weapons.GetValueOrDefault(Canonicalize(weaponCode), Unknown);

    public string Canonicalize(string? weaponCode)
    {
        if (string.IsNullOrWhiteSpace(weaponCode)) return Unknown.Code;
        string normalized = weaponCode.Trim().ToLowerInvariant();
        if (normalized.StartsWith("weapon_", StringComparison.Ordinal))
            normalized = normalized["weapon_".Length..];
        if (normalized.StartsWith("knife", StringComparison.Ordinal)) return "knife";
        if (Aliases.TryGetValue(normalized, out string? alias)) normalized = alias;
        return Weapons.ContainsKey(normalized) ? normalized : Unknown.Code;
    }

    public static IReadOnlyList<WeaponSequenceSegment> BuildSequence(
        IReadOnlyList<KillDescriptor> kills,
        IWeaponCatalog? catalog = null)
    {
        catalog ??= new WeaponCatalog();
        List<WeaponSequenceSegment> result = [];
        foreach (KillDescriptor kill in kills.OrderBy(value => value.Tick).ThenBy(value => value.EventIndex))
        {
            WeaponMetadata weapon = catalog.Resolve(kill.WeaponCode);
            if (result.Count > 0 &&
                string.Equals(result[^1].WeaponCode, weapon.Code, StringComparison.Ordinal))
            {
                WeaponSequenceSegment previous = result[^1];
                result[^1] = previous with { KillCount = previous.KillCount + 1 };
            }
            else
            {
                result.Add(new WeaponSequenceSegment(
                    weapon.Code,
                    weapon.DisplayName,
                    weapon.IconPath,
                    1,
                    result.Count > 0));
            }
        }
        return result;
    }

    private static WeaponMetadata Meta(
        string code,
        string name,
        WeaponCategory category) =>
        new(code, name, $"/assets/weapons/{code}.svg", category);
}
