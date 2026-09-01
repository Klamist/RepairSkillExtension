using System.Reflection;
using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace Ciallo.RepairExpansion;

public record ModMetadata : IModMetadata
{
    public  string ModGuid { get; init; } = "ciallo.repairextension";
    public  string Name { get; init; } = "Repair Skill Extension";
    public  string Author { get; init; } = "CialloMako";
    public  List<string>? Contributors { get; init; }
    public  Version Version { get; init; } = new("1.3.1");
    public  Range SptVersion { get; init; } = new("~4.1");
    public  List<string>? Incompatibilities { get; init; }
    public  Dictionary<string, Range>? ModDependencies { get; init; }
    public  string? Url { get; init; }
    public  bool? IsBundleMod { get; init; }
    public  string License { get; init; } = "MIT";
    public bool HasPrepatcher {get; init; } = false;

    public static readonly string ResourcesDirectory =
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Resources");
}
