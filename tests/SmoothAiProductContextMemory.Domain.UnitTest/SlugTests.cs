using SmoothAiProductContextMemory.Domain;

namespace SmoothAiProductContextMemory.Domain.UnitTest;

public class SlugTests
{
    [Theory]
    [InlineData("The quick brown fox", "the-quick-brown-fox")]
    [InlineData("  Leading and trailing  ", "leading-and-trailing")]
    [InlineData("Multiple   spaces", "multiple-spaces")]
    [InlineData("Symbols!@#$%^&*()", "symbols")]
    [InlineData("Café déjà vu", "cafe-deja-vu")]
    [InlineData("Already-kebab-cased", "already-kebab-cased")]
    [InlineData("UPPER CASE", "upper-case")]
    [InlineData("123 numbers 456", "123-numbers-456")]
    public void Subject_normalises_to_expected_slug(string input, string expected)
    {
        Slug.Subject(input).ShouldBe(expected);
    }

    [Fact]
    public void Subject_is_deterministic()
    {
        const string input = "The PostgreSQL persistence layer";
        Slug.Subject(input).ShouldBe(Slug.Subject(input));
    }

    [Fact]
    public void Subject_always_produces_slug_chars_only()
    {
        foreach (char c in Slug.Subject("Mixed!@# case & stuff"))
        {
            (char.IsLetterOrDigit(c) || c == '-').ShouldBeTrue();
        }
    }

    [Fact]
    public void Subject_rejects_null_description()
    {
        Should.Throw<ArgumentNullException>(() => Slug.Subject(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    [InlineData("?!@#$%^&*()")]
    public void Subject_rejects_descriptions_that_would_slug_to_empty(string input)
    {
        // An empty slug is not merely useless: every letterless description would collapse to the
        // same value and collide on the unique (group_id, subject_slug) backstop, so two unrelated
        // subjects would be treated as duplicates of one another.
        Should.Throw<ArgumentException>(() => Slug.Subject(input));
    }
}
