using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.Tests;

public sealed class SpotlightAndQrTests
{
    [Theory]
    [InlineData("25*4", 100)]
    [InlineData("15% of 200", 30)]
    [InlineData("sqrt(144) + 8", 20)]
    [InlineData("100 : 4", 25)]
    public void Spotlight_TryEvaluateMath_ComputesExpectedResult(string expr, double expected)
    {
        bool ok = SpotlightSearchService.TryEvaluateMath(expr, out double actual, out _);
        Assert.True(ok);
        Assert.Equal(expected, actual, precision: 4);
    }

    [Fact]
    public void Spotlight_TryEvaluateMath_RejectsNonMathProse()
    {
        Assert.False(SpotlightSearchService.TryEvaluateMath("notepad", out _, out _));
        Assert.False(SpotlightSearchService.TryEvaluateMath("open chrome", out _, out _));
    }

    [Fact]
    public void Spotlight_CalculateMatchScore_RanksExactAbovePrefixAcronymAndSubstring()
    {
        int exact = SpotlightSearchService.CalculateMatchScore("Visual Studio Code", "Visual Studio Code");
        int prefix = SpotlightSearchService.CalculateMatchScore("Visual Studio Code", "Visu");
        int acronym = SpotlightSearchService.CalculateMatchScore("Visual Studio Code", "vsc");
        int substr = SpotlightSearchService.CalculateMatchScore("Visual Studio Code", "Studio");
        int miss = SpotlightSearchService.CalculateMatchScore("Visual Studio Code", "xyz");

        Assert.True(exact > prefix);
        Assert.True(prefix > acronym);
        Assert.True(acronym > substr);
        Assert.True(substr > miss);
        Assert.Equal(0, miss);
    }

    [Fact]
    public void QrCodeGenerator_ProducesValidFinderPatternsAndDeterministicMatrix()
    {
        string url = "http://192.168.1.10:52341/s/a1b2c3d4";
        bool[,] m1 = QrCodeGenerator.Encode(url);
        bool[,] m2 = QrCodeGenerator.Encode(url);

        int size = m1.GetLength(0);
        Assert.Equal(size, m1.GetLength(1));
        Assert.InRange(size, 21, 41);

        // Deterministic output
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Assert.Equal(m1[y, x], m2[y, x]);
            }
        }

        // Top-left 7x7 finder pattern outer ring is all dark
        for (int i = 0; i < 7; i++)
        {
            Assert.True(m1[0, i]);
            Assert.True(m1[6, i]);
            Assert.True(m1[i, 0]);
            Assert.True(m1[i, 6]);
        }

        // Center 3x3 of top-left finder pattern is dark
        Assert.True(m1[3, 3]);
    }
}
