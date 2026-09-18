namespace AudioSwitcher.Core;
public record DisplayPosition(string Id, int X, int Y);
public static class DisplayLayout
{
    public static DisplayPosition[] Rebase(IReadOnlyList<DisplayPosition> positions, string primaryId)
    {
        var primary = positions.FirstOrDefault(p => p.Id == primaryId) ?? throw new ArgumentException("Монитор больше не подключён.");
        return positions.Select(p => new DisplayPosition(p.Id, checked(p.X - primary.X), checked(p.Y - primary.Y))).ToArray();
    }
}
