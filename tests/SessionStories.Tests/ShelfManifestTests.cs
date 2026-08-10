using SessionStories.Providers.Voices;

namespace SessionStories.Tests;

/// <summary>
/// Checks the shipped shelf rather than a fixture, because the shelf is content and content is
/// what goes wrong: an entry was once added ahead of its sample, and the only symptom was a play
/// button that answered 404 in the add-voice dialog. Nothing else in the build looks at these
/// files, so nothing else would have noticed.
/// </summary>
public sealed class ShelfManifestTests
{
    private static readonly string GalleryRoot = FindGalleryRoot();

    private static string FindGalleryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StoryMode.slnx")))
                return Path.Combine(dir.FullName, "voice-gallery");
        }
        throw new InvalidOperationException("could not find StoryMode.slnx above the test binary");
    }

    private static ShelfVoiceGallery Gallery() => new(GalleryRoot);

    [Fact]
    public void EveryOfferHasASampleOnDisk()
    {
        var missing = Gallery().ListLanguages()
            .SelectMany(language => Gallery().ListOffers(language.Locale))
            .Where(offer => Gallery().SamplePath(offer.Key) is not { } path || !File.Exists(path))
            .Select(offer => offer.Key)
            .ToList();

        // A voice you cannot audition is worse than one that is not offered: the reader picks it
        // blind and then waits out a whole render to find out what it sounds like.
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryOfferHasWhatItsInstallerWillAskFor()
    {
        var offers = Gallery().ListLanguages()
            .SelectMany(language => Gallery().ListOffers(language.Locale))
            .ToList();

        Assert.NotEmpty(offers);
        foreach (var offer in offers)
        {
            var plan = offer.Plan;
            var hasSource =
                plan.Speaker is { Length: > 0 } ||
                plan.DownloadUrl is { Length: > 0 } ||
                plan.ReferenceWavFile is { Length: > 0 };
            Assert.True(hasSource,
                $"'{offer.Key}' names no built-in speaker, no download and no reference recording, " +
                "so nothing could install it.");

            if (plan.ReferenceWavFile is { Length: > 0 } wav)
            {
                Assert.True(File.Exists(Path.Combine(GalleryRoot, "wavs", wav)),
                    $"'{offer.Key}' points at a reference recording that is not in the gallery: {wav}");
            }
        }
    }
}
