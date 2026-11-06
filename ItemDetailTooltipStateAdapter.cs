using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TreeTools.Lifecycle;

namespace TreeTools;

public sealed unsafe class ItemDetailTooltipStateAdapter : IDisposable
{
    // Categories 1-4 are racing chocobo registration and breeding records that legitimately use the rating group.
    private static readonly HashSet<uint> ChocoboRegistrationItemIDs =
        LuminaGetter.Get<RacingChocoboItem>()
            .Where(row => row.Category is >= 1 and <= 4)
            .Select(row => row.Item.RowId)
            .Where(itemID => itemID != 0)
            .ToHashSet();

    private readonly AddonEventRegistry addonEvents;
    private bool disposed;

    public ItemDetailTooltipStateAdapter()
    {
        addonEvents = new(DService.Instance().AddonLifecycle);
        addonEvents.Register(AddonEvent.PostRequestedUpdate, ITEM_DETAIL_ADDON_NAME, OnItemDetailPostRequestedUpdate);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        addonEvents.Dispose();
    }

    private static void OnItemDetailPostRequestedUpdate(AddonEvent _, AddonArgs args)
    {
        var addon = (AddonItemDetail*)args.Addon.Address;
        if (addon == null || !((AtkUnitBase*)addon)->IsAddonAndNodesReady())
            return;

        var itemID = ItemUtil.GetBaseId(AgentItemDetail.Instance()->ItemId).ItemId;
        if (itemID == 0 || ChocoboRegistrationItemIDs.Contains(itemID))
            return;

        var numberArrayData = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.ItemDetail);
        var stringArrayData = AtkStage.Instance()->GetStringArrayData(StringArrayType.ItemDetail);
        if (numberArrayData == null || numberArrayData->IntArray == null ||
            stringArrayData == null || stringArrayData->StringArray == null)
        {
            return;
        }

        var flags = (TooltipItemGroupFlags)numberArrayData->IntArray[ITEM_DETAIL_FLAGS_INDEX];
        if ((flags & CHOCOBO_RATING_GROUP_FLAGS) != CHOCOBO_RATING_GROUP_FLAGS)
            return;

        var chocoboRatingTitle = LuminaWrapper.GetAddonText(CHOCOBO_RATING_TITLE_ROW_ID);
        if (chocoboRatingTitle.Length == 0)
            return;

        ref var titleText = ref stringArrayData->StringArray[(int)TooltipItemType.SpecialTitle];
        if (!titleText.HasValue)
            return;

        var title = new ReadOnlySeString(new CStringPointer(titleText.Value).AsSpan());
        if (!title.ExtractText().Equals(chocoboRatingTitle, StringComparison.Ordinal))
            return;

        numberArrayData->IntArray[ITEM_DETAIL_FLAGS_INDEX] = (int)(flags & ~CHOCOBO_RATING_GROUP_FLAGS);
        addon->GenerateItemTooltip(numberArrayData, stringArrayData);
        addon->UpdateGroupPositions(numberArrayData, stringArrayData);
    }

    #region 常量

    private const string ITEM_DETAIL_ADDON_NAME = "ItemDetail";

    private const uint CHOCOBO_RATING_TITLE_ROW_ID = 8361;

    private const int ITEM_DETAIL_FLAGS_INDEX = 5;

    private const TooltipItemGroupFlags CHOCOBO_RATING_GROUP_FLAGS =
        TooltipItemGroupFlags.HeaderStatsGroup | TooltipItemGroupFlags.Effects;

    #endregion
}
