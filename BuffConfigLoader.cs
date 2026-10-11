using SPTarkov.Common.Extensions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using ConfigBonusSettings = SPTarkov.Server.Core.Models.Spt.Config.BonusSettings;
using System.Reflection;

namespace Ciallo.RepairExpansion;

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class BuffConfigLoader(
    RepairConfig repairConfig,
    GlobalTable globalTable,
    ISptLogger<BuffConfigLoader> logger,
    ModHelper modHelper
) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            var cfg = modHelper.GetJsonDataFromFile<BuffConfigFile>(pathToMod, "buffs.jsonc");
            ApplyBuffSettings(cfg, globalTable);
            ApplyRepairKitSettings(cfg, repairConfig);
            logger.Success("[Ciallo] Repair buffs.jsonc applied successfully.");
        }
        catch (Exception ex)
        {
            logger.Error($"[Ciallo] Error loading buffs.jsonc: {ex}");
        }
        return Task.CompletedTask;
    }

    private void ApplyBuffSettings(BuffConfigFile cfg, GlobalTable globals)
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

    private static void ApplyRepairKitSettings(BuffConfigFile cfg, RepairConfig repairConfig)
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