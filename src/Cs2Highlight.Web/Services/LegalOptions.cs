namespace Cs2Highlight.Web.Services;

public sealed class LegalOptions
{
    public string PrivacyPolicyVersion { get; set; } = "1.0";
    public string PersonalDataVersion { get; set; } = "1.0";
    public string ReferralRulesVersion { get; set; } = "1.0";
    public string EffectiveDate { get; set; } = "2026-01-01";
    public string OperatorName { get; set; } = "[Укажите оператора]";
    public string OperatorAddress { get; set; } = "[Укажите адрес]";
}
