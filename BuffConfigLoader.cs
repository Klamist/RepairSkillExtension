using SPTarkov.Common.Extensions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using ConfigBonusSettings = SPTarkov.Server.Core.Models.Spt.Config.BonusSettings;
using System.Text.Json;

namespace Ciallo.RepairExpansion;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class BuffConfigLoader(
    ConfigServer configServer,
    DatabaseService databaseService,
    ISptLogger<BuffConfigLoader> logger
) : IOnLoad
{
    private readonly RepairConfig _repairConfig = configServer.GetConfig<RepairConfig>();

    private static readonly JsonSerializerOptions _jsonOptions = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public Task OnLoad()
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "user", "mods", "RepairSkillExtension", "buffs.jsonc"
            );

            if (!File.Exists(path))
            {
                logger.Error($"[Ciallo] buffs.jsonc not found: {path}");
                return Task.CompletedTask;
            }

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<BuffConfigFile>(json, _jsonOptions);

            if (cfg == null)
            {
                logger.Error("[Ciallo] Failed to parse buffs.jsonc");
                return Task.CompletedTask;
            }

            ApplyBuffSettings(cfg);
            ApplyRepairKitSettings(cfg);

            logger.Success("[Ciallo] Repair buffs.jsonc applied successfully.");
        }
        catch (Exception ex)
        {
            logger.Error($"[Ciallo] Error loading buffs.jsonc: {ex}");
        }

        return Task.CompletedTask;
    }

    private void ApplyBuffSettings(BuffConfigFile cfg)
    {
        var globals = databaseService.GetGlobals();
        var skills = globals.Configuration.SkillsSettings;
        var dict = skills.GetAllPropertiesAsDictionary();

        foreach (var armorSkillName in new[] { "LightVests", "HeavyVests" })
        {
            if (dict[armorSkillName] is ArmorSkills armorSkill)
            {
                armorSkill.BuffSettings.CommonBuffChanceLevelBonus = cfg.BuffSettings.CommonBuffChanceLevelBonus;
                armorSkill.BuffSettings.CommonBuffMinChanceValue = cfg.BuffSettings.CommonBuffMinChanceValue;
                armorSkill.BuffSettings.ReceivedDurabilityMaxPercent = cfg.BuffSettings.ReceivedDurabilityMaxPercent;
            }
            else
                logger.Warning($"[Ciallo] {armorSkillName} is not ArmorSkills");
        }

        if (dict["WeaponTreatment"] is WeaponTreatment weaponSkill)
        {
            weaponSkill.BuffSettings.CommonBuffChanceLevelBonus = cfg.BuffSettings.CommonBuffChanceLevelBonus;
            weaponSkill.BuffSettings.CommonBuffMinChanceValue = cfg.BuffSettings.CommonBuffMinChanceValue;
            weaponSkill.BuffSettings.ReceivedDurabilityMaxPercent = cfg.BuffSettings.ReceivedDurabilityMaxPercent;
        }
        else
            logger.Warning("[Ciallo] WeaponTreatment is not WeaponTreatment type");
    }

    private void ApplyRepairKitSettings(BuffConfigFile cfg)
    {
        _repairConfig.RepairKit.Armor = cfg.repairKit.armors;
        _repairConfig.RepairKit.Vest = cfg.repairKit.armors;
        _repairConfig.RepairKit.Headwear = cfg.repairKit.armors;
        _repairConfig.RepairKit.Weapon = cfg.repairKit.weapon;
    }
}

public class BuffConfigFile
{
    public BuffSettingsData BuffSettings { get; set; }
    public RepairKitConfig repairKit { get; set; }
}

public class BuffSettingsData
{
    public double CommonBuffChanceLevelBonus { get; set; }
    public double CommonBuffMinChanceValue { get; set; }
    public double CurrentDurabilityLossToRemoveBuff { get; set; }
    public double MaxDurabilityLossToRemoveBuff { get; set; }
    public double RareBuffChanceCoff { get; set; }
    public double ReceivedDurabilityMaxPercent { get; set; }
}

public class RepairKitConfig
{
    public ConfigBonusSettings armors { get; set; }
    public ConfigBonusSettings weapon { get; set; }
}
