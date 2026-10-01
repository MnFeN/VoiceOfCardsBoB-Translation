namespace VoiceOfCardsLocalizationTool;

internal sealed class TranslationRow
{
    public required string Key { get; set; }
    public required string English { get; set; }
    public required string Japanese { get; set; }
    public required string Target { get; set; }
}

internal sealed class LocalizedEntry
{
    public long Id { get; set; }
    public required string Key { get; set; }
    public string? Value { get; set; }
}
