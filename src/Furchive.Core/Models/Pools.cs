namespace Furchive.Core.Models;

/// <summary>
/// Minimal information about an e621 pool used for listing and selection.
/// </summary>
public class PoolInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int PostCount { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public int? CreatorId { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public string? Category { get; set; }
    public string? PostIdsRaw { get; set; }
}
