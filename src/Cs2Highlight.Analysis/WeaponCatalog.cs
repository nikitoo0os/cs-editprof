namespace Cs2Highlight.Analysis;

public interface IWeaponCatalog
{
    WeaponMetadata Resolve(string? weaponCode);
    string Canonicalize(string? weaponCode);
}

public sealed class WeaponCatalog : IWeaponCatalog
{
    private static readonly WeaponMetadata Unknown =
        new("unknown", "Оружие не определено", "/assets/weapons/unknown.svg", WeaponCategory.Unknown);

    private static readonly Dictionary<string, WeaponMetadata> Weapons =
        new Dictionary<string, WeaponMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["ak47"] = Meta("ak47", "AK-47", WeaponCategory.Rifle),
            ["aug"] = Meta("aug", "AUG", WeaponCategory.Rifle),
            ["famas"] = Meta("famas", "FAMAS", WeaponCategory.Rifle),
            ["galilar"] = Meta("galilar", "Galil AR", WeaponCategory.Rifle),
            ["m4a4"] = Meta("m4a4", "M4A4", WeaponCategory.Rifle),
            ["m4a1"] = Meta("m4a1", "M4A1", WeaponCategory.Rifle),
            ["m4a1_silencer"] = Meta("m4a1_silencer", "M4A1-S", WeaponCategory.Rifle),
            ["sg556"] = Meta("sg556", "SG 553", WeaponCategory.Rifle),
            ["awp"] = Meta("awp", "AWP", WeaponCategory.Sniper),
            ["g3sg1"] = Meta("g3sg1", "G3SG1", WeaponCategory.Sniper),
            ["scar20"] = Meta("scar20", "SCAR-20", WeaponCategory.Sniper),
            ["ssg08"] = Meta("ssg08", "SSG 08", WeaponCategory.Sniper),
            ["deagle"] = Meta("deagle", "Desert Eagle", WeaponCategory.Pistol),
            ["elite"] = Meta("elite", "Dual Berettas", WeaponCategory.Pistol),
            ["fiveseven"] = Meta("fiveseven", "Five-SeveN", WeaponCategory.Pistol),
            ["glock"] = Meta("glock", "Glock-18", WeaponCategory.Pistol),
            ["hkp2000"] = Meta("hkp2000", "P2000", WeaponCategory.Pistol),
            ["p250"] = Meta("p250", "P250", WeaponCategory.Pistol),
            ["revolver"] = Meta("revolver", "R8 Revolver", WeaponCategory.Pistol),
            ["tec9"] = Meta("tec9", "Tec-9", WeaponCategory.Pistol),
            ["cz75a"] = Meta("cz75a", "CZ75-Auto", WeaponCategory.Pistol),
            ["usp_silencer"] = Meta("usp_silencer", "USP-S", WeaponCategory.Pistol),
            ["mac10"] = Meta("mac10", "MAC-10", WeaponCategory.Smg),
            ["mp5sd"] = Meta("mp5sd", "MP5-SD", WeaponCategory.Smg),
            ["mp7"] = Meta("mp7", "MP7", WeaponCategory.Smg),
            ["mp9"] = Meta("mp9", "MP9", WeaponCategory.Smg),
            ["p90"] = Meta("p90", "P90", WeaponCategory.Smg),
            ["ppbizon"] = Meta("ppbizon", "PP-Bizon", WeaponCategory.Smg),
            ["ump45"] = Meta("ump45", "UMP-45", WeaponCategory.Smg),
            ["m249"] = Meta("m249", "M249", WeaponCategory.Heavy),
            ["mag7"] = Meta("mag7", "MAG-7", WeaponCategory.Heavy),
            ["negev"] = Meta("negev", "Negev", WeaponCategory.Heavy),
            ["nova"] = Meta("nova", "Nova", WeaponCategory.Heavy),
            ["sawedoff"] = Meta("sawedoff", "Sawed-Off", WeaponCategory.Heavy),
            ["xm1014"] = Meta("xm1014", "XM1014", WeaponCategory.Heavy),
            ["knife"] = Meta("knife", "Knife", WeaponCategory.Knife),
            ["taser"] = Meta("taser", "Zeus x27", WeaponCategory.Equipment),
            ["hegrenade"] = Meta("hegrenade", "Осколочная граната", WeaponCategory.Equipment),
            ["flashbang"] = Meta("flashbang", "Световая граната", WeaponCategory.Equipment),
            ["smokegrenade"] = Meta("smokegrenade", "Дымовая граната", WeaponCategory.Equipment),
            ["molotov"] = Meta("molotov", "Молотов", WeaponCategory.Equipment),
            ["incgrenade"] = Meta("incgrenade", "Зажигательная граната", WeaponCategory.Equipment),
            ["decoy"] = Meta("decoy", "Ложная граната", WeaponCategory.Equipment),
            ["breachcharge"] = Meta("breachcharge", "Пролом", WeaponCategory.Equipment),
            ["c4"] = Meta("c4", "C4", WeaponCategory.Equipment)
        };

    private static readonly Dictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ak 47"] = "ak47",
            ["aug"] = "aug",
            ["famas"] = "famas",
            ["galil ar"] = "galilar",
            ["galil"] = "galilar",
            ["m4a1"] = "m4a1",
            ["m4a4"] = "m4a4",
            ["m4a1 s"] = "m4a1_silencer",
            ["m4a1 silencer"] = "m4a1_silencer",
            ["m4a1 silencer off"] = "m4a1_silencer",
            ["sg 553"] = "sg556",
            ["g3sg1"] = "g3sg1",
            ["scar 20"] = "scar20",
            ["desert eagle"] = "deagle",
            ["dual berettas"] = "elite",
            ["five seven"] = "fiveseven",
            ["glock 18"] = "glock",
            ["p2000"] = "hkp2000",
            ["r8 revolver"] = "revolver",
            ["usp s"] = "usp_silencer",
            ["usp silencer"] = "usp_silencer",
            ["usp silencer off"] = "usp_silencer",
            ["cz75 auto"] = "cz75a",
            ["ssg 08"] = "ssg08",
            ["scout"] = "ssg08",
            ["zeus x27"] = "taser",
            ["zeus"] = "taser",
            ["mac 10"] = "mac10",
            ["mp5 sd"] = "mp5sd",
            ["pp bizon"] = "ppbizon",
            ["bizon"] = "ppbizon",
            ["ump 45"] = "ump45",
            ["mag 7"] = "mag7",
            ["sawed off"] = "sawedoff",
            ["he grenade"] = "hegrenade",
            ["high explosive grenade"] = "hegrenade",
            ["smoke grenade"] = "smokegrenade",
            ["incendiary grenade"] = "incgrenade",
            ["decoy grenade"] = "decoy",
            ["breach charge"] = "breachcharge",
            ["c4"] = "c4"
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
        if (Weapons.ContainsKey(normalized)) return normalized;
        normalized = normalized.Replace('_', ' ').Replace('-', ' ');
        normalized = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
