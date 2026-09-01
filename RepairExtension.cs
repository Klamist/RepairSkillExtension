using SPTarkov.Common.Extensions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Repair;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;
using ConfigBonusSettings = SPTarkov.Server.Core.Models.Spt.Config.BonusSettings;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ciallo.repairextension;

[Injectable]
public class RepairCallbacks(
    ISptLogger<CialloRepairItemEventRouter> logger,
    EventOutputHolder eventOutputHolder,
    RepairService repairService,
    RepairConfig repairConfig,
    TemplateTable templateTable,
    GlobalTable globals,
    ItemHelper itemHelper,
    ProfileHelper profileHelper,
    RandomUtil randomUtil,
    WeightedRandomHelper weightedRandomHelper,
    ServerLocalisationService localisationService
)
{
    public ValueTask<ItemEventRouterResponse> HandleRepairWithKit(
        MongoId sessionId,
        PmcData pmcData,
        RepairActionDataRequest body
    )
    {
        var output = eventOutputHolder.GetOutput(sessionId);

        var repairDetails = repairService.RepairItemByKit(
            sessionId,
            pmcData,
            body.RepairKitsInfo,
            body.Target.Value,
            output
        );

        // 顶满耐久：只在 RepairKit 修理时生效
        if (repairDetails.RepairedItem?.Upd?.Repairable is not null)
        {
            repairDetails.RepairedItem.Upd.Repairable.Durability =
                repairDetails.RepairedItem.Upd.Repairable.MaxDurability;
        }

        repairService.AddBuffToItem(repairDetails, pmcData);

        output.ProfileChanges[sessionId].Items.ChangedItems.Add(repairDetails.RepairedItem);

        repairService.AddRepairSkillPoints(sessionId, repairDetails, pmcData);

        TryAddArmorXpForFaceCoverAndFriends(repairDetails, pmcData);
        TryAddBuffForFaceCoverAndVisor(repairDetails, pmcData);

        return new ValueTask<ItemEventRouterResponse>(output);
    }

    public ValueTask<ItemEventRouterResponse> HandleTraderRepair(
        MongoId sessionId,
        PmcData pmcData,
        TraderRepairActionDataRequest request
    )
    {
        var output = eventOutputHolder.GetOutput(sessionId);

        foreach (var repairItem in request.RepairItems)
        {
            var repairDetails = repairService.RepairItemByTrader(sessionId, pmcData, repairItem, request.TraderId);

            repairService.PayForRepair(sessionId, pmcData, repairItem.Id, repairDetails.RepairCost.Value, request.TraderId, output);

            if (output.Warnings?.Count > 0)
            {
                return new ValueTask<ItemEventRouterResponse>(output);
            }

            output.ProfileChanges[sessionId].Items.ChangedItems.Add(repairDetails.RepairedItem);

            repairService.AddRepairSkillPoints(sessionId, repairDetails, pmcData);
        }

        return new ValueTask<ItemEventRouterResponse>(output);
    }

    private void TryAddArmorXpForFaceCoverAndFriends(RepairDetails repairDetails, PmcData pmcData)
    {
        if (!repairDetails.RepairedByKit.GetValueOrDefault(false))
            return;

        var tpl = repairDetails.RepairedItem.Template;

        var isArmorLike =
            itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMOR) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMORED_EQUIPMENT) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.VEST) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.HEADWEAR) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.FACE_COVER) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.VISORS);

        if (!isArmorLike)
            return;

        var itemsDb = templateTable.Items;
        if (!itemsDb.TryGetValue(tpl, out var itemTemplate))
        {
            logger.Error(localisationService.GetText("repair-unable_to_find_item_in_db", tpl.ToString()));
            return;
        }

        var armorType = itemTemplate.Properties.ArmorType;
        var vestSkillToLevel = armorType == "Heavy" ? SkillTypes.HeavyVests : SkillTypes.LightVests;

        if (repairDetails.RepairPoints is null)
        {
            logger.Error(localisationService.GetText("repair-item_has_no_repair_points", tpl.ToString()));
            return;
        }

        var pointsToAdd = repairDetails.RepairPoints.Value * repairConfig.ArmorKitSkillPointGainPerRepairPointMultiplier;

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[Ciallo] Added extra armor XP: {pointsToAdd} to {vestSkillToLevel} for {tpl}");
        }

        profileHelper.AddSkillPointsToPlayer(pmcData, vestSkillToLevel, pointsToAdd, false);
    }

    private void TryAddBuffForFaceCoverAndVisor(RepairDetails repairDetails, PmcData pmcData)
    {
        if (!repairDetails.RepairedByKit.GetValueOrDefault(false))
            return;

        var tpl = repairDetails.RepairedItem.Template;

        if (!itemHelper.IsOfBaseclass(tpl, BaseClasses.FACE_COVER)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.VISORS)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMORED_EQUIPMENT))
        {
            return;
        }

        if (!ShouldBuffFaceCoverLikeItem(repairDetails, pmcData))
            return;

        var headwearCfg = repairConfig.RepairKit.Headwear;
        AddBuff(headwearCfg, repairDetails.RepairedItem);

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[Ciallo] Applied headwear buff config to FaceCover/Visor {tpl}");
        }
    }

    private bool ShouldBuffFaceCoverLikeItem(RepairDetails repairDetails, PmcData pmcData)
    {
        var hasTemplate = itemHelper.GetItem(repairDetails.RepairedItem.Template);
        if (!hasTemplate.Key)
        {
            return false;
        }

        var template = hasTemplate.Value;

        var armorType = template.Properties.ArmorType;
        var itemSkillType = armorType == "Heavy" ? SkillTypes.HeavyVests : SkillTypes.LightVests;

        if (pmcData.GetSkillFromProfile(itemSkillType)?.Progress < 1000)
        {
            return false;
        }

        var skillSettings = globals.Configuration.SkillsSettings.GetAllPropertiesAsDictionary();
        BuffSettings? buffSettings = ((ArmorSkills)skillSettings[itemSkillType.ToString()]).BuffSettings;

        var commonBuffMinChanceValue = buffSettings.CommonBuffMinChanceValue;
        var commonBuffChanceLevelBonus = buffSettings.CommonBuffChanceLevelBonus;
        var receivedDurabilityMaxPercent = buffSettings.ReceivedDurabilityMaxPercent;

        var skillLevel = Math.Truncate((pmcData.GetSkillFromProfile(itemSkillType)?.Progress ?? 0) / 100);

        if (repairDetails.RepairPoints is null)
        {
            logger.Error(localisationService.GetText("repair-item_has_no_repair_points", repairDetails.RepairedItem.Template.ToString()));
            return false;
        }

        var durabilityToRestorePercent = repairDetails.RepairPoints / template.Properties.MaxDurability;
        var durabilityMultiplier = GetDurabilityMultiplier(receivedDurabilityMaxPercent / 100.0, durabilityToRestorePercent.Value);

        var doBuff = commonBuffMinChanceValue + commonBuffChanceLevelBonus * skillLevel * durabilityMultiplier;
        var random = new Random();
        return random.NextDouble() <= doBuff;
    }

    private double GetDurabilityMultiplier(double receivedDurabilityMaxPercent, double durabilityToRestorePercent)
    {
        var clamped = Math.Min(durabilityToRestorePercent, receivedDurabilityMaxPercent);
        return clamped / receivedDurabilityMaxPercent;
    }

    private void AddBuff(ConfigBonusSettings itemConfig, Item item)
    {
        var bonusRarityName = weightedRandomHelper.GetWeightedValue(itemConfig.RarityWeight);
        var bonusTypeName = weightedRandomHelper.GetWeightedValue(itemConfig.BonusTypeWeight);

        var bonusRarity = bonusRarityName == "Rare" ? itemConfig.Rare : itemConfig.Common;
        var bonusValues = bonusRarity[bonusTypeName].ValuesMinMax;
        var bonusValue = randomUtil.GetDouble(bonusValues.Min, bonusValues.Max);

        var bonusThresholdPercents = bonusRarity[bonusTypeName].ActiveDurabilityPercentMinMax;
        var bonusThresholdPercent = randomUtil.GetDouble(bonusThresholdPercents.Min, bonusThresholdPercents.Max);

        item.Upd ??= new Upd();
        item.Upd.Buff = new UpdBuff
        {
            Rarity = bonusRarityName,
            BuffType = Enum.Parse<RepairBuffType>(bonusTypeName),
            Value = bonusValue,
            ThresholdDurability = randomUtil.GetPercentOfValue(
                bonusThresholdPercent,
                item.Upd.Repairable.Durability.Value,
                0
            ),
        };
    }
}

[Injectable(TypePriority = OnLoadOrder.Routers)]
public class CialloRepairItemEventRouter(RepairCallbacks callbacks)
    : ItemEventRouter([
        new ItemRouteAction<RepairActionDataRequest>(
            ItemEventActions.REPAIR,
            async (url, pmcData, body, sessionID, output, cancellationToken) =>
                await callbacks.HandleRepairWithKit(sessionID, pmcData, body)
        ),
        new ItemRouteAction<TraderRepairActionDataRequest>(
            ItemEventActions.TRADER_REPAIR,
            async (url, pmcData, body, sessionID, output, cancellationToken) =>
                await callbacks.HandleTraderRepair(sessionID, pmcData, body)
        ),
    ]) { }
