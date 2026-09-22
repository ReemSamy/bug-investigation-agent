using BugInvestigationAgent.Core.Configuration;

namespace BugInvestigationAgent.Core.Tests;

public class JiraUrlNormalizerTests
{
    [Test]
    public void Normalizes_board_url_to_site_root()
    {
        var input = "https://ehsq.atlassian.net/jira/software/c/projects/VKMS/boards/11";
        Assert.That(JiraUrlNormalizer.NormalizeBaseUrl(input), Is.EqualTo("https://ehsq.atlassian.net"));
    }

    [Test]
    public void Extracts_issue_key_with_space_or_dash()
    {
        Assert.That(JiraUrlNormalizer.ExtractIssueKey("https://ehsq.atlassian.net/browse/VKMS 4180"), Is.EqualTo("VKMS-4180"));
        Assert.That(JiraUrlNormalizer.ExtractIssueKey("VKMS-4180"), Is.EqualTo("VKMS-4180"));
        Assert.That(JiraUrlNormalizer.ExtractIssueKey("vkms_4180"), Is.EqualTo("VKMS-4180"));
    }
}
