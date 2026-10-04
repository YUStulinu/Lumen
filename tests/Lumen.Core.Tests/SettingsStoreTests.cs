using Lumen.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumen-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ReversingProtector _protector = new();

    private string FilePath => Path.Combine(_directory, "settings.json");

    private SettingsStore CreateStore() => new(FilePath, _protector, NullLogger<SettingsStore>.Instance);

    [Fact]
    public void A_missing_file_is_created_with_defaults()
    {
        AppSettings settings = CreateStore().Load();

        Assert.True(File.Exists(FilePath));
        Assert.Equal("Ctrl+Shift+Space", settings.Hotkeys.Ask);
        Assert.Equal("claude-opus-5-5", settings.Claude.Model);
        Assert.Equal(RoutingMode.LocalFirst, settings.RoutingMode);
    }

    [Fact]
    public void Saved_values_round_trip_and_enums_are_stored_as_text()
    {
        SettingsStore store = CreateStore();
        AppSettings settings = store.Load();
        settings.RoutingMode = RoutingMode.CloudOnly;
        settings.Local.ModelPath = @"C:\models\phi";
        settings.CustomActions.Add(new Prompts.QuickAction("haiku", "Haiku", "Turn this into a haiku."));
        store.Save(settings);

        AppSettings reloaded = CreateStore().Load();

        Assert.Equal(RoutingMode.CloudOnly, reloaded.RoutingMode);
        Assert.Equal(@"C:\models\phi", reloaded.Local.ModelPath);
        Assert.Equal("Haiku", Assert.Single(reloaded.CustomActions).Label);
        Assert.Contains("\"cloudOnly\"", File.ReadAllText(FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_corrupt_file_is_backed_up_and_replaced_with_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json");

        AppSettings settings = CreateStore().Load();

        Assert.Equal(RoutingMode.LocalFirst, settings.RoutingMode);
        Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{ "local": { "maxContextTokens": 5, "temperature": 9 }, "claude": { "model": "", "effort": " HIGH " } }""");

        AppSettings settings = CreateStore().Load();

        Assert.Equal(512, settings.Local.MaxContextTokens);
        Assert.Equal(2.0, settings.Local.Temperature);
        Assert.Equal("claude-opus-5-5", settings.Claude.Model);
        Assert.Equal("high", settings.Claude.Effort);
    }

    [Fact]
    public void The_api_key_is_never_stored_in_clear_text()
    {
        SettingsStore store = CreateStore();
        AppSettings settings = store.Load();
        store.SetApiKey(settings, "sk-ant-secret");
        store.Save(settings);

        Assert.DoesNotContain("sk-ant-secret", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Equal("sk-ant-secret", SettingsStore.ResolveApiKey(CreateStore().Load(), _protector));
    }

    [Fact]
    public void Save_raises_Changed()
    {
        SettingsStore store = CreateStore();
        AppSettings settings = store.Load();
        AppSettings? received = null;
        store.Changed += (_, s) => received = s;

        store.Save(settings);

        Assert.Same(settings, received);
    }

    [Fact]
    public void Clone_is_a_deep_copy()
    {
        var original = new AppSettings();
        AppSettings copy = original.Clone();
        copy.Local.ModelPath = "changed";

        Assert.Equal("", original.Local.ModelPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
