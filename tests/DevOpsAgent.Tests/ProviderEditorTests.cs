using DevOpsAgent.Application.Llm;
using DevOpsAgent.Domain.Llm;
using DevOpsAgent.Web;

namespace DevOpsAgent.Tests;

/// <summary>T8.1: Settings › LLM connection never shows a form that does not belong to the Providers list.</summary>
public sealed class ProviderEditorTests
{
    [Fact]
    public void NoProviders_EmptyListAndAnEmptyAddProviderForm()
    {
        var editor = new ProviderEditor();

        editor.Load([]);

        Assert.Empty(editor.Providers);
        Assert.True(editor.IsNew);
        Assert.Null(editor.SelectedId);
        Assert.Null(editor.Form.Type); // not "Ollama", not "OpenRouter": nothing chosen yet
        Assert.Equal(string.Empty, editor.Form.Name);
        Assert.Equal(string.Empty, editor.Form.BaseUrl);
        Assert.True(editor.Form.IsDefault); // the first provider becomes the default
    }

    [Fact]
    public void Load_SelectsTheDefault_AndTheFormShowsExactlyItsValues()
    {
        var ollama = Summary("Ollama", ProviderType.Ollama, "http://ollama:11434/v1", isDefault: false);
        var openRouter = Summary("Cloud", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", isDefault: true);
        var editor = new ProviderEditor();

        editor.Load([ollama, openRouter]);

        Assert.Equal(openRouter.Id, editor.SelectedId);
        AssertFormShows(editor, openRouter);
        editor.Select(ollama.Id);
        AssertFormShows(editor, ollama);
    }

    [Fact]
    public void Reload_KeepsTheSelection_OrFallsBackWhenItWasRemoved()
    {
        var first = Summary("A", ProviderType.Ollama, "http://a:11434/v1", isDefault: true);
        var second = Summary("B", ProviderType.LmStudio, "http://b:1234/v1", isDefault: false);
        var editor = new ProviderEditor();
        editor.Load([first, second]);
        editor.Select(second.Id);

        editor.Load([first, second with { Name = "B renamed" }]);
        Assert.Equal(second.Id, editor.SelectedId);
        Assert.Equal("B renamed", editor.Form.Name);

        editor.Load([first]);
        Assert.Equal(first.Id, editor.SelectedId);
        AssertFormShows(editor, first);

        editor.Load([]);
        Assert.True(editor.IsNew);
        Assert.Null(editor.Form.Type);
    }

    [Fact]
    public void AddProvider_ClearsTheSelection_AndChoosingATypeFillsItsDefaults()
    {
        var existing = Summary("Local", ProviderType.Ollama, "http://ollama:11434/v1", isDefault: true);
        var editor = new ProviderEditor();
        editor.Load([existing]);

        editor.StartNew();
        Assert.True(editor.IsNew);
        Assert.False(editor.Form.IsDefault);
        Assert.Equal(string.Empty, editor.Form.BaseUrl);

        editor.Form.Type = ProviderType.OpenRouter;
        editor.Form.ApplyTypeDefaults();
        Assert.Equal("https://openrouter.ai/api/v1", editor.Form.BaseUrl);
        Assert.Equal("OpenRouter", editor.Form.Name);
        editor.Form.Name = "My router";
        editor.Form.Type = ProviderType.Ollama;
        editor.Form.ApplyTypeDefaults();
        Assert.Equal("My router", editor.Form.Name); // a name the owner typed is kept
        Assert.Equal("http://ollama:11434/v1", editor.Form.BaseUrl);
    }

    [Fact]
    public void SelectingAnUnknownProvider_EmptiesTheForm()
    {
        var editor = new ProviderEditor();
        editor.Load([Summary("A", ProviderType.Ollama, "http://a:11434/v1", isDefault: true)]);

        editor.Select(Guid.NewGuid());

        Assert.True(editor.IsNew);
        Assert.Null(editor.Form.Type);
    }

    private static void AssertFormShows(ProviderEditor editor, LlmProviderSummary provider)
    {
        Assert.False(editor.IsNew);
        Assert.Equal(provider.Type, editor.Form.Type);
        Assert.Equal(provider.Name, editor.Form.Name);
        Assert.Equal(provider.BaseUrl, editor.Form.BaseUrl);
        Assert.Equal(provider.Model, editor.Form.Model);
        Assert.Equal(provider.IsDefault, editor.Form.IsDefault);
        Assert.Same(editor.Providers.Single(item => item.Id == provider.Id), editor.Selected);
    }

    private static LlmProviderSummary Summary(string name, ProviderType type, string baseUrl, bool isDefault) =>
        new(Guid.NewGuid(), name, type, baseUrl, "model-1", 8192, 0.2, 60, isDefault, false, true, true, true,
            null, null, null, null, null, null);
}
