using System.Reflection;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Ciallo.RepairExpansion;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "ciallo.repairextension";
    public string Name { get; init; } = "Repair Skill Extension";
    public string Author { get; init; } = "CialloMako";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.4.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
    public static readonly string ResourcesDirectory =
        Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "Resources"
        );
}