using SPTarkov.Server.Core.Models.Spt.Mod;

namespace PresetTrader;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.sp.bela.presettrader";
    public string Name { get; init; } = "PresetTrader";
    public string Author { get; init; } = "Bela";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.2.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.2");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/925316/SPTarkov.PresetTrader";
    public string License { get; init; } = "AGPL-3.0";
    public bool HasPrepatcher { get; init; }
}
