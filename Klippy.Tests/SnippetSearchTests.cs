using System;
using System.Collections.Generic;
using System.Linq;
using Klippy.Models;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

public class SnippetSearchTests
{
    private static List<SnippetSearch.Entry<Snippet>> Index(params Snippet[] snippets) =>
        snippets.Select(s => new SnippetSearch.Entry<Snippet>(s)).ToList();

    private static Snippet LogFiles => new()
    {
        Label = "Send log files",
        Content = "Please send us the log files, as per the instructions here:\n\nhttps://www.shineforms.co.uk/docs/XXX",
        Tag = "work",
        QuickCode = "slf",
    };

    private static Snippet DockerPrune => new()
    {
        Label = "Docker prune",
        Content = "docker system prune -af --volumes",
        Tag = "dev",
        QuickCode = "dp",
    };

    private static Snippet WorkEmail => new()
    {
        Label = "Work email",
        Content = "sam.rivera@northwind.io",
        Tag = "work",
    };

    [Fact]
    public void Tokenize_SplitsOnNonAlphanumeric_AndLowercases()
    {
        Assert.Equal(new[] { "send", "log", "files" }, SnippetSearch.Tokenize("Send LOG-files!"));
        Assert.Equal(new[] { "www", "shineforms", "co", "uk", "docs" },
            SnippetSearch.Tokenize("www.shineforms.co.uk/docs"));
        Assert.Empty(SnippetSearch.Tokenize("  \t\n"));
    }

    [Fact]
    public void MultiTokenPrefixes_FindContentWords()
    {
        var results = SnippetSearch.Search(Index(LogFiles, DockerPrune, WorkEmail), "log fil");
        Assert.Single(results);
        Assert.Equal("Send log files", results[0].Item.Label);
    }

    [Fact]
    public void EveryTokenMustMatch()
    {
        var results = SnippetSearch.Search(Index(LogFiles, DockerPrune), "log zebra");
        Assert.Empty(results);
    }

    [Fact]
    public void QuickCode_ExactMatch_RanksFirst()
    {
        // "slf" matches nothing by words but is the log-files snippet's quick-code.
        var results = SnippetSearch.Search(Index(DockerPrune, WorkEmail, LogFiles), "slf");
        Assert.Equal("Send log files", results[0].Item.Label);
    }

    [Fact]
    public void QuickCode_PrefixMatch_BeatsWordMatches()
    {
        // "d" prefix-matches DockerPrune's quick-code "dp" and also word-matches "docker"/"dev";
        // the quick-code hit must rank it above any plain word match.
        var other = new Snippet { Label = "Design doc", Content = "d is for design", Tag = "dev" };
        var results = SnippetSearch.Search(Index(other, DockerPrune), "d");
        Assert.Equal("Docker prune", results[0].Item.Label);
    }

    [Fact]
    public void QuickCodeQuery_WithSpaces_IsNotAQuickCode()
    {
        var results = SnippetSearch.Search(Index(LogFiles), "s lf");
        Assert.Empty(results); // "lf" prefixes no word; must not sneak in via quick-code
    }

    [Fact]
    public void LabelMatches_RankAboveContentMatches()
    {
        var contentOnly = new Snippet { Label = "Other", Content = "docker things", Tag = "dev" };
        var results = SnippetSearch.Search(Index(contentOnly, DockerPrune), "docker");
        Assert.Equal("Docker prune", results[0].Item.Label);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void EmptyQuery_ReturnsAll_MostRecentlyUsedFirst()
    {
        var older = new Snippet { Label = "Older", LastUsedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var newer = new Snippet { Label = "Newer", LastUsedAt = DateTimeOffset.UtcNow };
        var results = SnippetSearch.Search(Index(older, newer), "");
        Assert.Equal(new[] { "Newer", "Older" }, results.Select(r => r.Item.Label));
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var results = SnippetSearch.Search(Index(LogFiles), "SHINE");
        Assert.Single(results);
    }
}
