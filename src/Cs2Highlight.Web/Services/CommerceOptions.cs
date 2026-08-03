namespace Cs2Highlight.Web.Services;

public sealed class CommerceOptions
{
    public string SellerName { get; set; } = "[Укажите полное наименование продавца]";
    public string LegalAddress { get; set; } = "[Укажите юридический адрес]";
    public string Inn { get; set; } = "[Укажите ИНН]";
    public string SettlementAccount { get; set; } = "[Укажите расчётный счёт]";
    public string BankName { get; set; } = "[Укажите банк]";
    public string Bic { get; set; } = "[Укажите БИК]";
    public string CorrespondentAccount { get; set; } = "[Укажите корреспондентский счёт]";
    public string SupportEmail { get; set; } = "[Укажите email поддержки]";
    public string SupportPhone { get; set; } = "[Укажите телефон]";

    public bool IsDraft =>
        new[]
        {
            SellerName, LegalAddress, Inn, SupportEmail
        }.Any(value =>
            string.IsNullOrWhiteSpace(value) ||
            value.StartsWith('[') ||
            value.Contains("Укажите", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("SET_", StringComparison.OrdinalIgnoreCase));
}
