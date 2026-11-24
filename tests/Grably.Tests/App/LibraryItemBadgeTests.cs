using Grably.App.ViewModels;
using Grably.Core.Library;

namespace Grably.Tests.App;

public class LibraryItemBadgeTests
{
    [Theory]
    [InlineData(LibraryItemKind.Screenshot, null, null)]
    [InlineData(LibraryItemKind.Recording, 134_000L, "2:14")]
    [InlineData(LibraryItemKind.Recording, 3_725_000L, "1:02:05")]
    [InlineData(LibraryItemKind.Recording, null, "Video")]
    [InlineData(LibraryItemKind.Gif, 9_000L, "GIF")]
    public void Video_tiles_show_their_length_or_gif(LibraryItemKind kind, long? durationMs, string? badge) =>
        Assert.Equal(badge, new LibraryItemViewModel(new LibraryItem { Kind = kind, Title = "x", DurationMs = durationMs }).VideoBadge);
}
