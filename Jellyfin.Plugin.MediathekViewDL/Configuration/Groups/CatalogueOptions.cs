namespace Jellyfin.Plugin.MediathekViewDL.Configuration.Groups;

/// <summary>Options for the independently managed streaming catalogue.</summary>
public record CatalogueOptions
{
    /// <summary>Gets or sets a value indicating whether automatic catalogue sync is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the dedicated root containing Movies and Shows libraries.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the broadcaster names, separated by commas.</summary>
    public string Channels { get; set; } = "ARD,ZDF,3Sat,ARTE.DE,BR,HR,MDR,NDR,RBB,SR,SWR,WDR,ZDFneo,ZDFinfo,ONE,ARD-alpha";

    /// <summary>Gets or sets a value indicating whether reliably numbered series should also be included.</summary>
    public bool IncludeSeries { get; set; }

    /// <summary>Gets or sets the maximum API pages per broadcaster. Hitting this guard never retires files.</summary>
    public int MaxPages { get; set; } = 2000;

    /// <summary>Gets or sets the page size, independent of subscription search limits.</summary>
    public int PageSize { get; set; } = 500;

    /// <summary>Gets or sets the minimum time before a missing, repeatedly unavailable item can be retired.</summary>
    public int RetirementGraceHours { get; set; } = 72;
}
