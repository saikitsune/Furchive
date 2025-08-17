namespace Furchive.Core.Models;

/// <summary>
/// Minimal information about an e621 pool used for listing and selection.
/// </summary>
public class PoolInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int PostCount { get; set; }
}
