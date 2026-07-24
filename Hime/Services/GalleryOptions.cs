namespace Hime.Services;

/// <summary>鸣潮美图库配置。高热作品优先，官方角色立绘只用于全角色兜底。</summary>
public sealed class GalleryOptions
{
    public bool Enabled { get; set; } = true;
    public string CatalogPath { get; set; } = "resources/gallery/catalog.json";
    public int MinimumEngagement { get; set; } = 5000;
    public int RecentHistorySize { get; set; } = 20;
    public long MaxImageBytes { get; set; } = 12 * 1024 * 1024;
    public bool PreferHighEngagement { get; set; } = true;
    public bool AllowOfficialFallback { get; set; } = true;
}
