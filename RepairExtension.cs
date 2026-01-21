using SPTarkov.Common.Extensions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Repair;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using ConfigBonusSettings = SPTarkov.Server.Core.Models.Spt.Config.BonusSettings;
using LogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace ciallo.repairextension;

[Injectable(TypePriority = -100)]
public class CialloRepairItemEventRouter(
    ISptLogger<CialloRepairItemEventRouter> logger,
    EventOutputHolder eventOutputHolder,
    RepairService repairService,
    ItemHelper itemHelper,
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    ConfigServer configServer,
    RandomUtil randomUtil,
    WeightedRandomHelper weightedRandomHelper,
    ServerLocalisationService localisationService
) : ItemEventRouterDefinition
{
    private readonly RepairConfig _repairConfig = configServer.GetConfig<RepairConfig>();

    protected override List<HandledRoute> GetHandledRoutes()
    {
        return new()
        {
            new(ItemEventActions.REPAIR, false),
            new(ItemEventActions.TRADER_REPAIR, false)
        };
    }

    protected override ValueTask<ItemEventRouterResponse> HandleItemEventInternal(
        string url,
        PmcData pmcData,
        BaseInteractionRequestData body,
        MongoId sessionID,
        ItemEventRouterResponse output
    )
    {
        switch (url)
        {
            case ItemEventActions.REPAIR:
                return new ValueTask<ItemEventRouterResponse>(
                    HandleRepairWithKit(sessionID, pmcData, (RepairActionDataRequest)body)
                );
            case ItemEventActions.TRADER_REPAIR:
                return new ValueTask<ItemEventRouterResponse>(
                    HandleTraderRepair(sessionID, pmcData, (TraderRepairActionDataRequest)body)
                );
            default:
                throw new Exception($"CialloRepairItemEventRouter cannot handle route {url}");
        }
    }

    private ItemEventRouterResponse HandleRepairWithKit(
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

        repairService.AddBuffToItem(repairDetails, pmcData);

        output.ProfileChanges[sessionId].Items.ChangedItems.Add(repairDetails.RepairedItem);

        repairService.AddRepairSkillPoints(sessionId, repairDetails, pmcData);

        TryAddArmorXpForFaceCoverAndFriends(repairDetails, pmcData);
        TryAddBuffForFaceCoverAndVisor(repairDetails, pmcData);

        return output;
    }

    private ItemEventRouterResponse HandleTraderRepair(
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
                return output;
            }

            output.ProfileChanges[sessionId].Items.ChangedItems.Add(repairDetails.RepairedItem);

            repairService.AddRepairSkillPoints(sessionId, repairDetails, pmcData);
        }

        return output;
    }

    private void TryAddArmorXpForFaceCoverAndFriends(RepairDetails repairDetails, PmcData pmcData)
    {
        if (!repairDetails.RepairedByKit.GetValueOrDefault(false))
            return;

        var tpl = repairDetails.RepairedItem.Template;

        var isArmorLike =
            itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMOR) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.VEST) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.HEADWEAR) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.FACE_COVER) ||
            itemHelper.IsOfBaseclass(tpl, BaseClasses.VISORS);

        if (!isArmorLike)
            return;

        var itemsDb = databaseService.GetItems();
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

        var pointsToAdd = repairDetails.RepairPoints.Value * _repairConfig.ArmorKitSkillPointGainPerRepairPointMultiplier;

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[Ciallo] Added extra armor XP: {pointsToAdd} to {vestSkillToLevel} for {tpl}");
        }

        profileHelper.AddSkillPointsToPlayer(pmcData, vestSkillToLevel, pointsToAdd);
    }

    private void TryAddBuffForFaceCoverAndVisor(RepairDetails repairDetails, PmcData pmcData)
    {
        if (!repairDetails.RepairedByKit.GetValueOrDefault(false))
            return;

        var tpl = repairDetails.RepairedItem.Template;

        if (!itemHelper.IsOfBaseclass(tpl, BaseClasses.FACE_COVER)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.VISORS))
        {
            return;
        }

        if (!ShouldBuffFaceCoverLikeItem(repairDetails, pmcData))
            return;

        var headwearCfg = _repairConfig.RepairKit.Headwear;
        AddBuff(headwearCfg, repairDetails.RepairedItem);

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[Ciallo] Applied headwear buff config to FaceCover/Visor {tpl}");
        }
    }

    private bool ShouldBuffFaceCoverLikeItem(RepairDetails repairDetails, PmcData pmcData)
    {
        var globals = databaseService.GetGlobals();
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
        var durabilityMultiplier = GetDurabilityMultiplier(receivedDurabilityMaxPercent, durabilityToRestorePercent.Value);

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
