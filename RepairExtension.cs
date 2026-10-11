using SPTarkov.Common.Extensions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.DI.Routing;
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
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;
using ConfigBonusSettings = SPTarkov.Server.Core.Models.Spt.Config.BonusSettings;

namespace ciallo.repairextension;

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class CialloRepairItemEventRouter : ItemEventRouter
{
    private static CialloRepairItemEventRouter? _current;
    private readonly ISptLogger<CialloRepairItemEventRouter> logger;
    private readonly RepairService repairService;
    private readonly ItemHelper itemHelper;
    private readonly GlobalTable globalTable;
    private readonly TemplateTable templateTable;
    private readonly ProfileHelper profileHelper;
    private readonly RepairConfig repairConfig;
    private readonly RandomUtil randomUtil;
    private readonly WeightedRandomHelper weightedRandomHelper;
    private readonly ServerLocalisationService localisationService;

    public CialloRepairItemEventRouter(
        ISptLogger<CialloRepairItemEventRouter> logger,
        RepairService repairService,
        ItemHelper itemHelper,
        GlobalTable globalTable,
        TemplateTable templateTable,
        ProfileHelper profileHelper,
        RepairConfig repairConfig,
        RandomUtil randomUtil,
        WeightedRandomHelper weightedRandomHelper,
        ServerLocalisationService localisationService
    ) : base(CreateRoutes())
    {
        this.logger = logger;
        this.repairService = repairService;
        this.itemHelper = itemHelper;
        this.globalTable = globalTable;
        this.templateTable = templateTable;
        this.profileHelper = profileHelper;
        this.repairConfig = repairConfig;
        this.randomUtil = randomUtil;
        this.weightedRandomHelper = weightedRandomHelper;
        this.localisationService = localisationService;
        _current = this;
    }

    private static ItemRouteAction[] CreateRoutes() =>
    [
        new ItemRouteAction<RepairActionDataRequest>(
            ItemEventActions.REPAIR,
            static (url, pmc, body, session, output, ct) =>
                _current!.HandleRepairWithKitRoute(url, pmc, body, session, output, ct)
        ),
        new ItemRouteAction<TraderRepairActionDataRequest>(
            ItemEventActions.TRADER_REPAIR,
            static (url, pmc, body, session, output, ct) =>
                _current!.HandleTraderRepairRoute(url, pmc, body, session, output, ct)
        )
    ];

    private ValueTask<ItemEventRouterResponse> HandleRepairWithKitRoute(
        string url,
        PmcData pmcData,
        RepairActionDataRequest body,
        MongoId sessionId,
        ItemEventRouterResponse output,
        CancellationToken cancellationToken
    )
    {
        var details = repairService.RepairItemByKit(
            sessionId,
            pmcData,
            body.RepairKitsInfo,
            body.Target.Value,
            output
        );

        if (details.RepairedItem?.Upd?.Repairable is not null)
        {
            details.RepairedItem.Upd.Repairable.Durability = details.RepairedItem.Upd.Repairable.MaxDurability;
        }

        repairService.AddBuffToItem(details, pmcData);
        output.ProfileChanges[sessionId].Items.ChangedItems.Add(details.RepairedItem);
        repairService.AddRepairSkillPoints(sessionId, details, pmcData);
        TryAddArmorXp(details, pmcData);
        TryAddFaceCoverBuff(details, pmcData);

        return ValueTask.FromResult(output);
    }

    private ValueTask<ItemEventRouterResponse> HandleTraderRepairRoute(
        string url,
        PmcData pmcData,
        TraderRepairActionDataRequest body,
        MongoId sessionId,
        ItemEventRouterResponse output,
        CancellationToken cancellationToken
    )
    {
        foreach (var repairItem in body.RepairItems)
        {
            var details = repairService.RepairItemByTrader(sessionId, pmcData, repairItem, body.TraderId);
            repairService.PayForRepair(
                sessionId,
                pmcData,
                repairItem.Id,
                details.RepairCost!.Value,
                body.TraderId,
                output
            );

            if (output.Warnings?.Count > 0)
            {
                return ValueTask.FromResult(output);
            }

            output.ProfileChanges[sessionId].Items.ChangedItems.Add(details.RepairedItem);
            repairService.AddRepairSkillPoints(sessionId, details, pmcData);
        }

        return ValueTask.FromResult(output);
    }

    private void TryAddArmorXp(RepairDetails details, PmcData pmcData)
    {
        if (!details.RepairedByKit.GetValueOrDefault())
        {
            return;
        }

        var tpl = details.RepairedItem.Template;
        if (!itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMOR)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMORED_EQUIPMENT)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.VEST)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.HEADWEAR)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.FACE_COVER)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.VISORS))
        {
            return;
        }

        if (!templateTable.Items.TryGetValue(tpl, out var itemTemplate))
        {
            logger.Error(localisationService.GetText("repair-unable_to_find_item_in_db", tpl.ToString()));
            return;
        }

        if (details.RepairPoints is null)
        {
            logger.Error(localisationService.GetText("repair-item_has_no_repair_points", tpl.ToString()));
            return;
        }

        var skill = itemTemplate.Properties.ArmorType == "Heavy"
            ? SkillTypes.HeavyVests
            : SkillTypes.LightVests;

        profileHelper.AddSkillPointsToPlayer(
            pmcData,
            skill,
            details.RepairPoints.Value * repairConfig.ArmorKitSkillPointGainPerRepairPointMultiplier,
            false
        );
    }

    private void TryAddFaceCoverBuff(RepairDetails details, PmcData pmcData)
    {
        if (!details.RepairedByKit.GetValueOrDefault())
        {
            return;
        }

        var tpl = details.RepairedItem.Template;
        if (!itemHelper.IsOfBaseclass(tpl, BaseClasses.FACE_COVER)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.VISORS)
            && !itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMORED_EQUIPMENT))
        {
            return;
        }

        var found = itemHelper.GetItem(tpl);
        if (!found.Key || details.RepairPoints is null)
        {
            return;
        }

        var template = found.Value;
        var skill = template.Properties.ArmorType == "Heavy"
            ? SkillTypes.HeavyVests
            : SkillTypes.LightVests;

        var skillProgress = pmcData.Skills?.Common?.FirstOrDefault(x => x.Id == skill)?.Progress ?? 0;
        if (skillProgress < 1000)
        {
            return;
        }

        var dict = globalTable.Configuration.SkillsSettings.GetAllPropertiesAsDictionary();
        if (dict[skill.ToString()] is not ArmorSkills armor)
        {
            return;
        }

        var multiplier = Math.Min(
            details.RepairPoints.Value / (double)template.Properties.MaxDurability,
            armor.BuffSettings.ReceivedDurabilityMaxPercent / 100.0
        ) / (armor.BuffSettings.ReceivedDurabilityMaxPercent / 100.0);

        var level = Math.Truncate(skillProgress / 100);
        if (new Random().NextDouble()
            > armor.BuffSettings.CommonBuffMinChanceValue
            + armor.BuffSettings.CommonBuffChanceLevelBonus * level * multiplier)
        {
            return;
        }

        AddBuff(repairConfig.RepairKit.Headwear, details.RepairedItem);
    }

    private void AddBuff(ConfigBonusSettings config, Item item)
    {
        var rarityName = weightedRandomHelper.GetWeightedValue(config.RarityWeight);
        var typeName = weightedRandomHelper.GetWeightedValue(config.BonusTypeWeight);
        var rarity = rarityName == "Rare" ? config.Rare : config.Common;
        var range = rarity[typeName].ValuesMinMax;

        item.Upd ??= new Upd();
        item.Upd.Buff = new UpdBuff
        {
            Rarity = rarityName,
            BuffType = Enum.Parse<RepairBuffType>(typeName),
            Value = randomUtil.GetDouble(range.Min, range.Max),
            ThresholdDurability = randomUtil.GetPercentOfValue(
                randomUtil.GetDouble(
                    rarity[typeName].ActiveDurabilityPercentMinMax.Min,
                    rarity[typeName].ActiveDurabilityPercentMinMax.Max
                ),
                item.Upd.Repairable!.Durability!.Value,
                0
            )
        };
    }
}