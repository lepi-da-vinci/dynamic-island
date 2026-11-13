using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.Tests;

public sealed class LyricsAndRomajiTests
{
    [Fact]
    public void ParseLrc_ExtractsSortedLinesWithTimestampsAndRomanizedTrack()
    {
        string lrc = """
            [ti:Yoru ni Kakeru]
            [00:12.50]沈むように溶けてゆくように
            [00:05.00]騒がしい日々に笑えない君に
            [00:20.00][00:40.00]二人だけの空が広がる夜に
            """;

        string roma = """
            [00:05.00]sawagashii hibi ni waraenai kimi ni
            [00:12.50]shizumu you ni tokete yuku you ni
            """;

        var lines = LyricsService.ParseLrc(lrc, roma);

        Assert.Equal(4, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(5.0), lines[0].Timestamp);
        Assert.Equal("騒がしい日々に笑えない君に", lines[0].Text);
        Assert.Equal("sawagashii hibi ni waraenai kimi ni", lines[0].RomanizedText);

        Assert.Equal(TimeSpan.FromSeconds(12.5), lines[1].Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(20.0), lines[2].Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(40.0), lines[3].Timestamp);
        Assert.Equal(lines[2].Text, lines[3].Text);
    }

    [Fact]
    public void GetActiveLyrics_ReturnsExpectedLineAndRatioAtPosition()
    {
        var service = new LyricsService();
        var parsed = LyricsService.ParseLrc("""
            [00:10.00]First line
            [00:20.00]Second line
            [00:30.00]Third line
            """);
        service.LoadLinesForTest(parsed);

        var intro = service.GetActiveLyrics(TimeSpan.FromSeconds(4));
        Assert.Equal(-1, intro.LineIndex);
        Assert.Equal("First line", intro.NextLine);

        var mid = service.GetActiveLyrics(TimeSpan.FromSeconds(15));
        Assert.Equal(0, mid.LineIndex);
        Assert.Equal("First line", mid.CurrentLine);
        Assert.Equal("Second line", mid.NextLine);
        Assert.InRange(mid.LineProgressRatio, 0.45, 0.60);
    }

    [Fact]
    public void RomajiService_HandlesHiraganaDeAndSokuonCorrectly()
    {
        string? singleDe = RomajiTransliterationService.Transliterate("で");
        Assert.Equal("de", singleDe);

        string? geminateDe = RomajiTransliterationService.Transliterate("っで");
        Assert.Equal("dde", geminateDe);

        string? phrase = RomajiTransliterationService.Transliterate("さくらが咲いている");
        Assert.NotNull(phrase);
        Assert.Contains("sakura", phrase, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("さくら", phrase);
    }

    [Fact]
    public void RomajiService_RomanizesKoreanHangulAndMandarinPinyin()
    {
        string? korean = RomajiTransliterationService.Transliterate("안녕하세요");
        Assert.NotNull(korean);
        Assert.False(RomajiTransliterationService.HasNonLatinScript(korean));
        Assert.Contains("an nyeong", korean, StringComparison.OrdinalIgnoreCase);

        string? mandarin = RomajiTransliterationService.Transliterate("我爱你");
        Assert.NotNull(mandarin);
        Assert.Contains("wo", mandarin, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ai", mandarin, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ni", mandarin, StringComparison.OrdinalIgnoreCase);
    }
}
