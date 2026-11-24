using System.Net;
using System.Net.Http;
using LightMarkdownWriter.Services;

namespace LightMarkdownWriter.Tests;

/// <summary>Distribution: the daily update check. No real network: a fake handler plays GitHub.</summary>
public sealed class Milestone13Tests
{
    private sealed class FakeGitHub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(respond());
        }
    }

    private static FakeGitHub Release(string tag, bool prerelease = false) => new(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/lucasriechelmann/LightMarkdownWriter/releases/tag/{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}}}"""),
    });

    private static readonly Version Current = new(1, 1, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("v1.2.0", true)]
    [InlineData("v1.1.1", true)]
    [InlineData("v2.0", true)]
    [InlineData("v1.1.0", false)]
    [InlineData("1.1", false)]
    [InlineData("v1.0.9", false)]
    [InlineData("v1.2.0-beta+abc", true)]
    [InlineData("nightly", false)]
    public void Tags_are_compared_as_major_minor_patch(string tag, bool newer)
    {
        Assert.Equal(newer, UpdateService.IsNewer(tag, Current));
    }

    [Fact]
    public async Task A_newer_release_is_reported_with_its_page()
    {
        var github = Release("v1.2.0");
        var update = await UpdateService.CheckAsync(new AppSettings(), Current, github, Now);

        Assert.Equal("1.2.0", update!.Version);
        Assert.EndsWith("/releases/tag/v1.2.0", update.Url);
        Assert.Equal(UpdateService.LatestReleaseApi, github.LastRequest!.RequestUri!.ToString());
        Assert.Contains("LightMarkdownWriter", github.LastRequest.Headers.UserAgent.ToString()); // GitHub requires a User-Agent
    }

    [Fact]
    public async Task Github_is_asked_at_most_once_a_day_and_the_known_update_still_shows()
    {
        var settings = new AppSettings();
        var github = Release("v1.2.0");

        await UpdateService.CheckAsync(settings, Current, github, Now);
        var later = await UpdateService.CheckAsync(settings, Current, github, Now.AddHours(20));
        Assert.Equal(1, github.Calls);
        Assert.Equal("1.2.0", later!.Version); // from the cached result

        await UpdateService.CheckAsync(settings, Current, github, Now.AddHours(25));
        Assert.Equal(2, github.Calls);
    }

    [Fact]
    public async Task Nothing_shows_when_up_to_date_turned_off_or_offline()
    {
        Assert.Null(await UpdateService.CheckAsync(new AppSettings(), Current, Release("v1.1.0"), Now));

        var off = Release("v9.0.0");
        Assert.Null(await UpdateService.CheckAsync(new AppSettings { CheckForUpdates = false }, Current, off, Now));
        Assert.Equal(0, off.Calls); // turned off means no request at all

        var settings = new AppSettings();
        var offline = new FakeGitHub(() => throw new HttpRequestException("No such host is known."));
        Assert.Null(await UpdateService.CheckAsync(settings, Current, offline, Now));
        Assert.Null(settings.LastUpdateCheckUtc); // a failed check isn't recorded, so the next start tries again

        var rateLimited = new FakeGitHub(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.Null(await UpdateService.CheckAsync(new AppSettings(), Current, rateLimited, Now));
    }

    [Theory]
    [InlineData("""{"tag_name":"v2.0.0","draft":true}""")]
    [InlineData("""{"tag_name":"v2.0.0","prerelease":true}""")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Drafts_prereleases_and_odd_responses_are_ignored(string json)
    {
        Assert.Null(UpdateService.ParseLatest(json));
    }

    [Fact]
    public void Only_github_release_pages_are_opened()
    {
        var parsed = UpdateService.ParseLatest("""{"tag_name":"v2.0.0","html_url":"https://evil.example/download"}""");
        Assert.Equal(UpdateService.ReleasesPage, parsed!.Value.Url);
    }
}
