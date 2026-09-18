namespace AudioSwitcher.Platform;
public record DeviceOption(string Id, string Name, string Details, bool IsDefault, bool IsDisplay = false)
{
    private int Parenthesis => Name.IndexOf(" (", StringComparison.Ordinal);
    public string DisplayName => IsDisplay ? Name.Split(" · ")[0] : Parenthesis > 0 ? Name[..Parenthesis] : Name;
    public string DisplayDetails => IsDisplay && Name.Contains(" · ") ? $"{Name[(Name.IndexOf(" · ", StringComparison.Ordinal) + 3)..]} · {Details}" : Parenthesis > 0 && Name.EndsWith(')') ? Name[(Parenthesis + 2)..^1] : Details;
    public string Glyph => IsDisplay || Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "\uE7F4" : "\uE767";
    public string Marker => IsDefault ? IsDisplay ? "Главный" : "Используется" : "";
}
