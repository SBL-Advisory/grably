namespace Grably.Core;

/// <summary>
/// Grably's colours, which are those of Strategies Beyond Limits (SBL), as 0xRRGGBB. See assets/brand/README.md;
/// tools/brand/build_brand.py keeps its own copy for drawing the logo.
/// </summary>
public static class BrandColors
{
    /// <summary>SBL navy: the UI accent.</summary>
    public const int Navy = 0x243E8A;

    /// <summary>A mid SBL navy that shows on both light and dark content.</summary>
    public const int NavyMid = 0x5B78D0;

    /// <summary>SBL orange: marks drawn over screenshots and video. Never for the recording state.</summary>
    public const int Orange = 0xF38120;
}
