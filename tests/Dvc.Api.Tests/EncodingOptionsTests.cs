using Dvc.Api.Models.Options;
using Microsoft.Extensions.Configuration;

namespace Dvc.Api.Tests;

public class EncodingOptionsTests
{
    private static EncodingOptions Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection(EncodingOptions.SectionName)
            .Get<EncodingOptions>() ?? new EncodingOptions();

    [Fact]
    public void Caps_BindFromConfig()
    {
        var options = Bind(new()
        {
            ["Encoding:DefaultMaxShortSide"] = "1080",
            ["Encoding:DefaultMaxFps"] = "29.97",
        });

        Assert.Equal(1080, options.DefaultMaxShortSide);
        Assert.Equal(29.97, options.DefaultMaxFps);
    }

    [Fact]
    public void Caps_AreDisabled_WhenLeftEmpty()
    {
        // An empty value (e.g. "Encoding__DefaultMaxShortSide=" as an env var
        // override) must disable the cap rather than fail to bind.
        var options = Bind(new()
        {
            ["Encoding:DefaultMaxShortSide"] = "",
            ["Encoding:DefaultMaxFps"] = "",
        });

        Assert.Null(options.DefaultMaxShortSide);
        Assert.Null(options.DefaultMaxFps);
    }

    [Fact]
    public void Preset_FallsBackToDefault_WhenUnset()
    {
        var options = Bind(new() { ["Encoding:TwoPass"] = "false" });

        Assert.Equal("slow", options.Preset);
    }
}
