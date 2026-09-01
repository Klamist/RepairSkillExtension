using System.Text.Json;
using SPTarkov.Common.Extensions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using ConfigBonusSettings = SPTarkov.Server.Core.Models.Spt.Config.BonusSettings;

namespace Ciallo.RepairExpansion;

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class BuffConfigLoader(
    RepairConfig repairConfig,
    GlobalTable globals,
    ISptLogger<BuffConfigLoader> logger
) : IOnLoad
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public Task OnLoadAsync(CancellationToken cancellationToken)
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
        repairConfig.RepairKit.Armor = cfg.repairKit.armors;
        repairConfig.RepairKit.Vest = cfg.repairKit.armors;
        repairConfig.RepairKit.Headwear = cfg.repairKit.armors;
        repairConfig.RepairKit.Weapon = cfg.repairKit.weapon;
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
