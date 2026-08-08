using SessionStories.Tts.Chatterbox;

namespace SessionStories.Tests;

public class SentenceChunkerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \n \n ")]
    public void EmptyOrWhitespaceInput_ReturnsEmptyList(string input)
    {
        Assert.Empty(SentenceChunker.Chunk(input));
    }

    [Fact]
    public void InvalidBounds_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SentenceChunker.Chunk("x", minChars: 0, maxChars: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SentenceChunker.Chunk("x", minChars: 10, maxChars: 5));
    }

    [Fact]
    public void ShortTextUnderMinChars_ProducesSingleChunkStartingParagraph()
    {
        var chunks = SentenceChunker.Chunk("Krótki tekst.");

        var chunk = Assert.Single(chunks);
        Assert.Equal("Krótki tekst.", chunk.Text);
        Assert.True(chunk.StartsParagraph);
    }

    [Fact]
    public void MergesSentencesGreedilyUntilMinCharsReached()
    {
        var a = new string('A', 19) + ".";
        var b = new string('B', 19) + ".";
        var c = new string('C', 19) + ".";
        var d = new string('D', 19) + ".";

        var chunks = SentenceChunker.Chunk($"{a} {b} {c} {d}", minChars: 30, maxChars: 200);

        Assert.Equal(2, chunks.Count);
        Assert.Equal($"{a} {b}", chunks[0].Text);
        Assert.Equal($"{c} {d}", chunks[1].Text);
        Assert.True(chunks[0].StartsParagraph);
        Assert.False(chunks[1].StartsParagraph);
    }

    [Fact]
    public void ExactFitAtMaxChars_MergesButOneOverDoesNot()
    {
        var a = new string('A', 19) + ".";
        var b = new string('B', 19) + ".";
        var text = $"{a} {b}"; // 41 chars merged

        var exactFit = SentenceChunker.Chunk(text, minChars: 41, maxChars: 41);
        var chunk = Assert.Single(exactFit);
        Assert.Equal(text, chunk.Text);

        var oneOver = SentenceChunker.Chunk(text, minChars: 40, maxChars: 40);
        Assert.Equal(2, oneOver.Count);
        Assert.Equal(a, oneOver[0].Text);
        Assert.Equal(b, oneOver[1].Text);
    }

    [Fact]
    public void OversizedSentence_SplitsAtClauseBoundaries()
    {
        var sentence = "Alfa bravo charlie delta, echo foxtrot golf hotel, india juliett kilo lima.";

        var chunks = SentenceChunker.Chunk(sentence, minChars: 5, maxChars: 40);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 40));
        Assert.EndsWith(",", chunks[0].Text);
        Assert.EndsWith(",", chunks[1].Text);
        Assert.Equal(sentence, string.Join(" ", chunks.Select(c => c.Text)));
        Assert.True(chunks[0].StartsParagraph);
        Assert.All(chunks.Skip(1), c => Assert.False(c.StartsParagraph));
    }

    [Fact]
    public void OversizedSentenceWithoutClausePunctuation_SplitsAtWordBoundaries()
    {
        var sentence = "Alfa bravo charlie delta echo foxtrot golf hotel india juliett kilo lima nova.";

        var chunks = SentenceChunker.Chunk(sentence, minChars: 5, maxChars: 30);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 30));
        // joining with a single space reconstructs the input, proving no word was cut
        Assert.Equal(sentence, string.Join(" ", chunks.Select(c => c.Text)));
    }

    [Fact]
    public void OversizedUnbrokenWord_HardSplitsAtMaxChars()
    {
        var sentence = new string('X', 100) + ".";

        var chunks = SentenceChunker.Chunk(sentence, minChars: 5, maxChars: 30);

        Assert.Equal(4, chunks.Count);
        Assert.Equal([30, 30, 30, 11], chunks.Select(c => c.Text.Length));
        Assert.Equal(sentence, string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public void ParagraphFirstChunkFlagged_OthersNot()
    {
        var chunks = SentenceChunker.Chunk("One two three. Four five six.\n\nSeven eight nine.", minChars: 1, maxChars: 350);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("One two three.", chunks[0].Text);
        Assert.True(chunks[0].StartsParagraph);
        Assert.Equal("Four five six.", chunks[1].Text);
        Assert.False(chunks[1].StartsParagraph);
        Assert.Equal("Seven eight nine.", chunks[2].Text);
        Assert.True(chunks[2].StartsParagraph);
    }

    [Fact]
    public void TinyParagraphsNeverMergeAcrossBlankLine()
    {
        // both paragraphs are far below the default minChars yet stay separate
        var chunks = SentenceChunker.Chunk("Ala ma kota.\r\n\r\nKot ma Alę.");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Ala ma kota.", chunks[0].Text);
        Assert.Equal("Kot ma Alę.", chunks[1].Text);
        Assert.True(chunks[0].StartsParagraph);
        Assert.True(chunks[1].StartsParagraph);
    }

    [Fact]
    public void PolishAbbreviationsAndInitials_DoNotEndSentences()
    {
        var text = "To np. prosty test dr. Kowalskiego, tzn. wszystko działa m.in. u J. Nowaka. Zażółć gęślą jaźń!";

        var chunks = SentenceChunker.Chunk(text, minChars: 1, maxChars: 350);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("To np. prosty test dr. Kowalskiego, tzn. wszystko działa m.in. u J. Nowaka.", chunks[0].Text);
        Assert.Equal("Zażółć gęślą jaźń!", chunks[1].Text);
    }

    [Fact]
    public void ClosingQuoteAfterTerminator_StaysWithItsSentence()
    {
        var chunks = SentenceChunker.Chunk("He said \"Stop!\" Then he left.", minChars: 1, maxChars: 350);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("He said \"Stop!\"", chunks[0].Text);
        Assert.Equal("Then he left.", chunks[1].Text);
    }

    [Fact]
    public void PolishClosingQuoteAfterTerminator_StaysWithItsSentence()
    {
        var chunks = SentenceChunker.Chunk("Powiedziała „Nie!” Potem wyszła.", minChars: 1, maxChars: 350);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Powiedziała „Nie!”", chunks[0].Text);
        Assert.Equal("Potem wyszła.", chunks[1].Text);
    }

    [Fact]
    public void QuotedExclamationFollowedByLowercase_DoesNotSplit()
    {
        var chunks = SentenceChunker.Chunk("He shouted \"Stop!\" and kept running.", minChars: 1, maxChars: 350);

        var chunk = Assert.Single(chunks);
        Assert.Equal("He shouted \"Stop!\" and kept running.", chunk.Text);
    }

    [Fact]
    public void EllipsisAndTerminatorRuns_EndSentences()
    {
        var chunks = SentenceChunker.Chunk("Czekaj… No dobrze. Co?! Serio?!", minChars: 1, maxChars: 350);

        Assert.Equal(["Czekaj…", "No dobrze.", "Co?!", "Serio?!"], chunks.Select(c => c.Text));
    }

    [Fact]
    public void InternalWhitespace_IsNormalizedToSingleSpaces()
    {
        var chunks = SentenceChunker.Chunk("Pierwsza  linia\ndruga\t\tlinia trzecia.");

        var chunk = Assert.Single(chunks);
        Assert.Equal("Pierwsza linia druga linia trzecia.", chunk.Text);
        Assert.True(chunk.StartsParagraph);
    }
}
