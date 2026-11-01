using EtlPipelines.Server.Agents;

namespace EtlPipelines.Server.Tests.Runs;

/// <summary>What a tag may look like.</summary>
[Category("Server")]
public class TagNamesTests
{
    [Test]
    public void Tags_are_trimmed_lower_cased_and_listed_once()
    {
        TagNames.TryNormalize(["  GPU ", "gpu", "Env:Prod", "", "   "], out var tags, out var error).Should().BeTrue();

        error.Should().BeNull();
        tags.Should().Equal("gpu", "env:prod");
    }

    [Test]
    [Arguments("has space")]
    [Arguments("semi;colon")]
    [Arguments("-leading-dash")]
    [Arguments("emoji🙂")]
    public void A_tag_with_anything_but_letters_digits_and_dot_underscore_colon_slash_dash_is_refused(string tag)
    {
        TagNames.TryNormalize([tag], out var tags, out var error).Should().BeFalse();

        error.Should().Contain("not a valid tag");
        tags.Should().BeEmpty();
    }

    [Test]
    public void A_tag_that_is_too_long_and_too_many_tags_are_refused()
    {
        TagNames.TryNormalize([new string('a', TagNames.MaxLength + 1)], out _, out var tooLong).Should().BeFalse();
        tooLong.Should().Contain("at most 50 characters");

        TagNames.TryNormalize(Enumerable.Range(0, TagNames.MaxCount + 1).Select(i => $"tag{i}"), out _, out var tooMany).Should().BeFalse();
        tooMany.Should().Contain("At most 20");

        TagNames.TryNormalize(Enumerable.Range(0, TagNames.MaxCount).Select(i => $"tag{i}"), out _, out _).Should().BeTrue();
    }

    [Test]
    public void Two_lists_are_joined_ignoring_case_and_repeats()
    {
        TagNames.Union(["Linux", "gpu"], ["GPU", "eu"]).Should().Equal("Linux", "gpu", "eu");
    }
}
